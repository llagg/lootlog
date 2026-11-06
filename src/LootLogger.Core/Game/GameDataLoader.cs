using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml;

namespace LootLogger.Core.Game;

public sealed class GameDatabase
{
    public GameDatabase(ItemDb items, IReadOnlyDictionary<string, string> clusterNames)
    {
        Items = items;
        ClusterNames = clusterNames;
    }

    public static GameDatabase Empty { get; } = new(ItemDb.Empty, new Dictionary<string, string>());

    /// <summary>Подпись для инстансов (данжи, острова, хайдауты). Интерфейс задаёт её на нужном языке.</summary>
    public static string InstanceLabel { get; set; } = "инстанс";

    public ItemDb Items { get; }
    public IReadOnlyDictionary<string, string> ClusterNames { get; }

    /// <summary>Название локации по индексу из пакетов игры.</summary>
    public string ClusterDisplayName(string clusterIndex, string islandName = "")
    {
        if (string.IsNullOrWhiteSpace(clusterIndex))
        {
            return string.Empty;
        }

        string name;
        if (ClusterNames.TryGetValue(clusterIndex, out var known))
        {
            name = known;
        }
        else
        {
            // Инстансы (данжи, острова, хайдауты) приходят в виде «база@тип@...».
            var basePart = clusterIndex.Split('@', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? clusterIndex;
            name = ClusterNames.TryGetValue(basePart, out var baseName) ? $"{baseName} ({InstanceLabel})" : clusterIndex;
        }

        return string.IsNullOrWhiteSpace(islandName) ? name : $"{name} ({islandName})";
    }
}

/// <summary>
/// Читает справочник предметов и локаций прямо из файлов игры (items.bin, localization.bin, world.bin)
/// и кэширует результат, чтобы последующие запуски занимали доли секунды.
/// </summary>
public static class GameDataLoader
{
    /// <summary>Ключи этапов загрузки (переводит интерфейс).</summary>
    public const string ProgressReadingNames = "LoadingNames";
    public const string ProgressReadingItems = "LoadingItems";
    public const string ProgressReadingClusters = "LoadingClusters";

    private const string CacheVersion = "v2";
    private const string ItemPrefix = "@ITEMS_";

    private static readonly byte[] Key = [48, 239, 114, 71, 66, 242, 4, 50];
    private static readonly byte[] Iv = [14, 166, 220, 137, 219, 237, 220, 79];

    /// <summary>
    /// Приводит выбранную пользователем папку к папке «game» (в ней лежит Albion-Online_Data).
    /// Возвращает null, если файлов игры там нет.
    /// </summary>
    public static string? ResolveGameDirectory(string? selectedPath)
    {
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return null;
        }

        try
        {
            var candidates = new List<string>();
            var dir = new DirectoryInfo(selectedPath.Trim().Trim('"'));
            for (var current = dir; current != null; current = current.Parent)
            {
                candidates.Add(current.FullName);
                candidates.Add(Path.Combine(current.FullName, "game"));
            }

            return candidates.FirstOrDefault(c => File.Exists(GetBinPath(c, "items.bin")));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Ищет установленную игру: запущенный процесс, затем стандартные пути.</summary>
    public static string? DetectGameDirectory()
    {
        try
        {
            foreach (var process in Process.GetProcessesByName("Albion-Online"))
            {
                using (process)
                {
                    try
                    {
                        var exePath = process.MainModule?.FileName;
                        var resolved = ResolveGameDirectory(Path.GetDirectoryName(exePath));
                        if (resolved != null)
                        {
                            return resolved;
                        }
                    }
                    catch
                    {
                        // нет доступа к процессу — пробуем дальше
                    }
                }
            }
        }
        catch
        {
            // ignored
        }

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new List<string>
        {
            Path.Combine(programFilesX86, "AlbionOnline"),
            Path.Combine(programFiles, "AlbionOnline"),
            Path.Combine(programFilesX86, "Albion Online"),
            Path.Combine(programFiles, "Albion Online"),
            Path.Combine(programFilesX86, "Steam", "steamapps", "common", "Albion Online"),
            Path.Combine(programFiles, "Steam", "steamapps", "common", "Albion Online"),
        };

        foreach (var drive in SafeDrives())
        {
            candidates.Add(Path.Combine(drive, "AlbionOnline"));
            candidates.Add(Path.Combine(drive, "Albion Online"));
            candidates.Add(Path.Combine(drive, "Games", "AlbionOnline"));
            candidates.Add(Path.Combine(drive, "Games", "Albion Online"));
            candidates.Add(Path.Combine(drive, "SteamLibrary", "steamapps", "common", "Albion Online"));
            candidates.Add(Path.Combine(drive, "Steam", "steamapps", "common", "Albion Online"));
        }

        return candidates.Select(ResolveGameDirectory).FirstOrDefault(x => x != null);
    }

    public static async Task<GameDatabase> LoadAsync(string gameDirectory, string cacheDirectory, IProgress<string>? progress = null)
    {
        return await Task.Run(() => Load(gameDirectory, cacheDirectory, progress)).ConfigureAwait(false);
    }

    private static GameDatabase Load(string gameDirectory, string cacheDirectory, IProgress<string>? progress)
    {
        var itemsBin = GetBinPath(gameDirectory, "items.bin");
        var localizationBin = GetBinPath(gameDirectory, "localization.bin");
        var worldBin = GetBinPath(gameDirectory, Path.Combine("cluster", "world.bin"));

        if (!File.Exists(itemsBin))
        {
            throw new FileNotFoundException("items.bin not found", itemsBin);
        }

        Directory.CreateDirectory(cacheDirectory);
        var stamp = string.Join('|', CacheVersion, Stamp(itemsBin), Stamp(localizationBin), Stamp(worldBin));
        var itemsCache = Path.Combine(cacheDirectory, "items-cache.tsv");
        var clustersCache = Path.Combine(cacheDirectory, "clusters-cache.tsv");

        var items = TryReadItemsCache(itemsCache, stamp);
        var clusters = TryReadClustersCache(clustersCache, stamp);
        if (items != null && clusters != null)
        {
            return new GameDatabase(new ItemDb(items), clusters);
        }

        progress?.Report(ProgressReadingNames);
        var names = File.Exists(localizationBin) ? ReadItemNames(localizationBin) : new Dictionary<string, (string Ru, string En)>();

        progress?.Report(ProgressReadingItems);
        items = ReadItems(itemsBin, names);

        progress?.Report(ProgressReadingClusters);
        clusters = File.Exists(worldBin) ? ReadClusters(worldBin) : new Dictionary<string, string>();

        try
        {
            WriteItemsCache(itemsCache, stamp, items);
            WriteClustersCache(clustersCache, stamp, clusters);
        }
        catch
        {
            // кэш не обязателен
        }

        return new GameDatabase(new ItemDb(items), clusters);
    }

    private static string GetBinPath(string gameDirectory, string fileName)
    {
        return Path.Combine(gameDirectory, "Albion-Online_Data", "StreamingAssets", "GameData", fileName);
    }

    private static string Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "none";
        }
        catch
        {
            return "none";
        }
    }

    private static IEnumerable<string> SafeDrives()
    {
        try
        {
            return DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
                .Select(d => d.RootDirectory.FullName)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    internal static byte[] DecryptBin(string path)
    {
        var encrypted = File.ReadAllBytes(path);
        using var des = DES.Create();
        des.Key = Key;
        des.IV = Iv;
        des.Mode = CipherMode.CBC;
        var decrypted = des.CreateDecryptor().TransformFinalBlock(encrypted, 0, encrypted.Length);

        using var gzip = new GZipStream(new MemoryStream(decrypted), CompressionMode.Decompress);
        using var output = new MemoryStream(decrypted.Length * 4);
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static Dictionary<string, (string Ru, string En)> ReadItemNames(string localizationBin)
    {
        var result = new Dictionary<string, (string Ru, string En)>(StringComparer.OrdinalIgnoreCase);
        var data = DecryptBin(localizationBin);

        using var reader = XmlReader.Create(new MemoryStream(data), new XmlReaderSettings
        {
            IgnoreWhitespace = true,
            IgnoreComments = true,
            DtdProcessing = DtdProcessing.Ignore,
            CheckCharacters = false
        });

        string? tuId = null;
        string ru = string.Empty, en = string.Empty;

        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                if (reader.Name == "tu")
                {
                    tuId = reader.GetAttribute("tuid");
                    ru = en = string.Empty;
                    if (tuId == null || !tuId.StartsWith(ItemPrefix, StringComparison.OrdinalIgnoreCase) || tuId.EndsWith("_DESC", StringComparison.OrdinalIgnoreCase))
                    {
                        tuId = null;
                    }
                }
                else if (reader.Name == "tuv" && tuId != null)
                {
                    var lang = reader.GetAttribute("xml:lang");
                    if (reader.Read() && reader.NodeType == XmlNodeType.Element && reader.Name == "seg")
                    {
                        var text = reader.ReadElementContentAsString();
                        if (string.Equals(lang, "RU-RU", StringComparison.OrdinalIgnoreCase))
                        {
                            ru = text;
                        }
                        else if (string.Equals(lang, "EN-US", StringComparison.OrdinalIgnoreCase))
                        {
                            en = text;
                        }
                    }
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.Name == "tu" && tuId != null)
            {
                result[tuId] = (Sanitize(ru), Sanitize(en));
                tuId = null;
            }
        }

        return result;
    }

    /// <summary>
    /// Порядок индексов повторяет порядок предметов в items.bin так же, как это делает сама игра
    /// (и Statistics Analysis Tool): базовый предмет, затем его зачарованные версии, в конце — пустые/полные журналы.
    /// </summary>
    internal static List<ItemInfo> ReadItems(string itemsBin, IReadOnlyDictionary<string, (string Ru, string En)> names)
    {
        var data = DecryptBin(itemsBin);
        return ParseItemsXml(data, names);
    }

    internal static List<ItemInfo> ParseItemsXml(byte[] data, IReadOnlyDictionary<string, (string Ru, string En)> names)
    {
        var text = Encoding.UTF8.GetString(RemoveBom(data));
        text = new string(text.Where(c => !char.IsControl(c) || char.IsWhiteSpace(c)).ToArray());

        var xml = new XmlDocument();
        xml.LoadXml(text);

        var items = new List<ItemInfo>(12000);
        var journals = new List<string>();
        var index = 1;

        ItemInfo Create(string uniqueName, string nameVariable, ItemCategory category = ItemCategory.Other)
        {
            var (ru, en) = names.TryGetValue(nameVariable, out var n) ? n : (string.Empty, string.Empty);
            return new ItemInfo(index++, uniqueName, ru, en, category);
        }

        var root = xml.LastChild;
        if (root?.ChildNodes == null)
        {
            return items;
        }

        foreach (XmlNode node in root.ChildNodes)
        {
            var uniqueName = node.Attributes?["uniquename"]?.Value;
            if (node.NodeType != XmlNodeType.Element || string.IsNullOrEmpty(uniqueName))
            {
                continue;
            }

            var enchantmentLevel = node.Attributes!["enchantmentlevel"]?.Value;
            var nameAttribute = node.Attributes["descvariable0"]?.Value;
            var suffix = enchantmentLevel != null && enchantmentLevel != "0" ? "@" + enchantmentLevel : string.Empty;

            var nameVariable = nameAttribute ?? ItemPrefix + uniqueName;
            if (uniqueName.Contains("ARTEFACT", StringComparison.Ordinal))
            {
                nameVariable = ItemPrefix + uniqueName;
            }

            var category = DetectCategory(node, uniqueName);
            items.Add(Create(uniqueName + suffix, nameVariable, category));

            if (node.Name == "journalitem")
            {
                journals.Add(uniqueName);
            }

            var enchantments = node.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.Name == "enchantments");
            if (enchantments == null)
            {
                continue;
            }

            foreach (var child in enchantments.ChildNodes.OfType<XmlElement>())
            {
                var level = child.Attributes["enchantmentlevel"]?.Value;
                items.Add(Create(uniqueName + "@" + level, nameAttribute ?? ItemPrefix + uniqueName, category));
            }
        }

        foreach (var journal in journals)
        {
            items.Add(Create(journal + "_EMPTY", ItemPrefix + journal + "_EMPTY"));
            items.Add(Create(journal + "_FULL", ItemPrefix + journal + "_FULL"));
        }

        return items;
    }

    /// <summary>
    /// Категория по слоту, в который предмет надевается. Инструменты для сбора (кирки, серпы, удочки,
    /// набор следопыта) тоже надеваются в слот оружия, но считаются «прочим».
    /// </summary>
    internal static ItemCategory DetectCategory(XmlNode node, string uniqueName)
    {
        var slot = node.Attributes?["slottype"]?.Value?.ToLowerInvariant();
        var shopCategory = node.Attributes?["shopcategory"]?.Value?.ToLowerInvariant();

        if (node.Name == "mount" || slot == "mount")
        {
            return ItemCategory.Mount;
        }

        if (slot is "potion" or "food")
        {
            return ItemCategory.Consumable;
        }

        if (shopCategory == "tools" || uniqueName.Contains("_TOOL_", StringComparison.OrdinalIgnoreCase))
        {
            return ItemCategory.Other;
        }

        return slot is "mainhand" or "offhand" or "head" or "armor" or "shoes" or "cape" or "bag"
            ? ItemCategory.Equipment
            : ItemCategory.Other;
    }

    private static Dictionary<string, string> ReadClusters(string worldBin)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var data = DecryptBin(worldBin);

        using var reader = XmlReader.Create(new MemoryStream(RemoveBom(data)), new XmlReaderSettings
        {
            IgnoreWhitespace = true,
            IgnoreComments = true,
            DtdProcessing = DtdProcessing.Ignore,
            CheckCharacters = false
        });

        while (reader.Read())
        {
            if (reader.NodeType != XmlNodeType.Element || reader.Name != "cluster")
            {
                continue;
            }

            var id = reader.GetAttribute("id");
            var name = reader.GetAttribute("displayname");
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
            {
                result.TryAdd(id, Sanitize(name));
            }
        }

        return result;
    }

    private static byte[] RemoveBom(byte[] data)
    {
        return data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF ? data[3..] : data;
    }

    private static string Sanitize(string value)
    {
        return value.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    private static List<ItemInfo>? TryReadItemsCache(string path, string stamp)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var reader = new StreamReader(path, Encoding.UTF8);
            if (reader.ReadLine() != stamp)
            {
                return null;
            }

            var items = new List<ItemInfo>(12000);
            while (reader.ReadLine() is { } line)
            {
                var parts = line.Split('\t');
                if (parts.Length >= 5 && int.TryParse(parts[0], out var index) && int.TryParse(parts[4], out var category))
                {
                    items.Add(new ItemInfo(index, parts[1], parts[2], parts[3], (ItemCategory) category));
                }
            }

            return items.Count > 0 ? items : null;
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, string>? TryReadClustersCache(string path, string stamp)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var reader = new StreamReader(path, Encoding.UTF8);
            if (reader.ReadLine() != stamp)
            {
                return null;
            }

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            while (reader.ReadLine() is { } line)
            {
                var parts = line.Split('\t');
                if (parts.Length >= 2)
                {
                    result.TryAdd(parts[0], parts[1]);
                }
            }

            return result;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteItemsCache(string path, string stamp, List<ItemInfo> items)
    {
        var temp = path + ".tmp";
        using (var writer = new StreamWriter(temp, false, new UTF8Encoding(false)))
        {
            writer.WriteLine(stamp);
            foreach (var item in items)
            {
                writer.Write(item.Index);
                writer.Write('\t');
                writer.Write(item.UniqueName);
                writer.Write('\t');
                writer.Write(item.NameRu);
                writer.Write('\t');
                writer.Write(item.NameEn);
                writer.Write('\t');
                writer.WriteLine((int) item.Category);
            }
        }

        File.Move(temp, path, true);
    }

    private static void WriteClustersCache(string path, string stamp, Dictionary<string, string> clusters)
    {
        var temp = path + ".tmp";
        using (var writer = new StreamWriter(temp, false, new UTF8Encoding(false)))
        {
            writer.WriteLine(stamp);
            foreach (var (id, name) in clusters)
            {
                writer.Write(id);
                writer.Write('\t');
                writer.WriteLine(name);
            }
        }

        File.Move(temp, path, true);
    }
}
