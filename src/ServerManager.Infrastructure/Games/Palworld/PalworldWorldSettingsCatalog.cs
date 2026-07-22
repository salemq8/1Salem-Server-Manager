using System.Globalization;
using ServerManager.Contracts;

namespace ServerManager.Infrastructure.Games.Palworld;

public static class PalworldWorldSettingsCatalog
{
    private static readonly IReadOnlyList<Definition> Definitions =
    [
        Number("ExpRate", "Progression", "Experience rate", "معدل الخبرة", "Multiplier for experience gained.", "مضاعف الخبرة المكتسبة.", 0.1, 20, "1.000000"),
        Number("PalCaptureRate", "Progression", "Pal capture rate", "معدل صيد البال", "Multiplier for Pal capture probability.", "مضاعف احتمالية صيد البال.", 0.1, 10, "1.000000"),
        Number("PalSpawnNumRate", "Progression", "Pal spawn rate", "معدل ظهور البال", "Multiplier for the number of spawned Pals.", "مضاعف عدد البال الظاهر.", 0.1, 3, "1.000000", warning: "Higher values increase CPU and memory load."),
        Number("PalEggDefaultHatchingTime", "Progression", "Huge egg hatching time", "وقت فقس البيضة الكبيرة", "Hours required to hatch a Huge Egg.", "الساعات المطلوبة لفقس بيضة كبيرة.", 0, 240, "72.000000", "hours"),
        Number("SupplyDropSpan", "Progression", "Supply drop interval", "فاصل إسقاط الإمدادات", "Minutes between meteorite or supply-drop events.", "الدقائق بين أحداث النيزك أو إسقاط الإمدادات.", 1, 10080, "180", "minutes"),

        Number("DayTimeSpeedRate", "Time", "Day speed", "سرعة النهار", "Daytime progression speed.", "سرعة مرور وقت النهار.", 0.1, 10, "1.000000"),
        Number("NightTimeSpeedRate", "Time", "Night speed", "سرعة الليل", "Nighttime progression speed.", "سرعة مرور وقت الليل.", 0.1, 10, "1.000000"),

        Number("PlayerDamageRateAttack", "Players", "Player damage dealt", "ضرر اللاعب", "Multiplier for damage dealt by players.", "مضاعف الضرر الذي يسببه اللاعبون.", 0.1, 10, "1.000000"),
        Number("PlayerDamageRateDefense", "Players", "Player damage taken", "الضرر الواقع على اللاعب", "Multiplier for damage taken by players.", "مضاعف الضرر الواقع على اللاعبين.", 0.1, 10, "1.000000"),
        Number("PlayerStaminaDecreaceRate", "Players", "Player stamina depletion", "استهلاك تحمل اللاعب", "Multiplier for player stamina depletion.", "مضاعف استهلاك تحمل اللاعب.", 0.1, 10, "1.000000"),
        Number("PlayerStomachDecreaceRate", "Players", "Player hunger depletion", "استهلاك جوع اللاعب", "Multiplier for player hunger depletion.", "مضاعف استهلاك جوع اللاعب.", 0.1, 10, "1.000000"),
        Number("PlayerAutoHPRegeneRate", "Players", "Player health regeneration", "تجدد صحة اللاعب", "Multiplier for natural player health regeneration.", "مضاعف تجدد صحة اللاعب الطبيعي.", 0, 10, "1.000000"),
        Number("PlayerAutoHpRegeneRateInSleep", "Players", "Player sleep regeneration", "تجدد اللاعب أثناء النوم", "Multiplier for player health regeneration while sleeping.", "مضاعف تجدد صحة اللاعب أثناء النوم.", 0, 10, "1.000000"),
        Enum("DeathPenalty", "Players", "Death penalty", "عقوبة الموت", "Items and Pals lost when a player dies.", "العناصر والبال المفقودة عند موت اللاعب.", "All", "None", "Item", "ItemAndEquipment", "All"),
        Boolean("bHardcore", "Players", "Hardcore", "وضع الهاردكور", "Players cannot respawn after death.", "لا يمكن للاعبين العودة بعد الموت.", false),

        Number("PalDamageRateAttack", "Pals", "Pal damage dealt", "ضرر البال", "Multiplier for damage dealt by Pals.", "مضاعف الضرر الذي يسببه البال.", 0.1, 10, "1.000000"),
        Number("PalDamageRateDefense", "Pals", "Pal damage taken", "الضرر الواقع على البال", "Multiplier for damage taken by Pals.", "مضاعف الضرر الواقع على البال.", 0.1, 10, "1.000000"),
        Number("PalStaminaDecreaceRate", "Pals", "Pal stamina depletion", "استهلاك تحمل البال", "Multiplier for Pal stamina depletion.", "مضاعف استهلاك تحمل البال.", 0.1, 10, "1.000000"),
        Number("PalStomachDecreaceRate", "Pals", "Pal hunger depletion", "استهلاك جوع البال", "Multiplier for Pal hunger depletion.", "مضاعف استهلاك جوع البال.", 0.1, 10, "1.000000"),
        Number("PalAutoHPRegeneRate", "Pals", "Pal health regeneration", "تجدد صحة البال", "Multiplier for natural Pal health regeneration.", "مضاعف تجدد صحة البال الطبيعي.", 0, 10, "1.000000"),
        Number("PalAutoHpRegeneRateInSleep", "Pals", "Palbox regeneration", "تجدد البال في الصندوق", "Multiplier for Pal health regeneration while sleeping in a Palbox.", "مضاعف تجدد صحة البال داخل صندوق البال.", 0, 10, "1.000000"),

        Number("CollectionDropRate", "Resources and Farming", "Gatherable item rate", "معدل موارد الجمع", "Multiplier for gatherable item quantity.", "مضاعف كمية موارد الجمع.", 0.1, 10, "1.000000"),
        Number("CollectionObjectHpRate", "Resources and Farming", "Gatherable object health", "صحة موارد الجمع", "Multiplier for gatherable object health.", "مضاعف صحة موارد الجمع.", 0.1, 10, "1.000000"),
        Number("CollectionObjectRespawnSpeedRate", "Resources and Farming", "Resource respawn interval", "فاصل عودة الموارد", "Multiplier for resource respawn interval.", "مضاعف فاصل عودة الموارد.", 0.1, 10, "1.000000"),
        Number("EnemyDropItemRate", "Resources and Farming", "Enemy item drops", "غنائم الأعداء", "Multiplier for enemy item drops.", "مضاعف غنائم الأعداء.", 0, 10, "1.000000"),
        Number("MonsterFarmActionSpeedRate", "Resources and Farming", "Ranch production speed", "سرعة إنتاج المزرعة", "Multiplier for item production from grazing.", "مضاعف سرعة إنتاج الرعي.", 0.1, 10, "1.000000"),
        Number("ItemWeightRate", "Resources and Farming", "Item weight", "وزن العناصر", "Multiplier for item weight.", "مضاعف وزن العناصر.", 0, 10, "1.000000"),
        Number("ItemCorruptionMultiplier", "Resources and Farming", "Item spoilage speed", "سرعة فساد العناصر", "Multiplier for item corruption speed.", "مضاعف سرعة فساد العناصر.", 0, 10, "1.000000"),

        Integer("BaseCampMaxNum", "Bases and Guilds", "Total server bases", "إجمالي قواعد الخادم", "Total number of bases across the server.", "إجمالي عدد القواعد على الخادم.", 1, 256, "128", warning: "Increasing the total base count raises server processing load."),
        Integer("BaseCampMaxNumInGuild", "Bases and Guilds", "Bases per guild", "القواعد لكل نقابة", "Maximum bases allowed per guild.", "الحد الأقصى للقواعد لكل نقابة.", 1, 10, "4", warning: "Higher guild base limits increase server processing load."),
        Integer("BaseCampWorkerMaxNum", "Bases and Guilds", "Workers per base", "العمال لكل قاعدة", "Maximum Pals assigned to one base.", "الحد الأقصى للبال العامل في القاعدة.", 1, 50, "15", warning: "More workers per base increase CPU and memory load."),
        Integer("GuildPlayerMaxNum", "Bases and Guilds", "Players per guild", "اللاعبون لكل نقابة", "Maximum players in one guild.", "الحد الأقصى للاعبين في النقابة.", 1, 100, "20"),
        Integer("MaxBuildingLimitNum", "Bases and Guilds", "Building limit per player", "حد المباني لكل لاعب", "Per-player building count; zero means unlimited.", "عدد مباني اللاعب؛ الصفر يعني غير محدود.", 0, 100000, "0", warning: "Large or unlimited building counts can increase server load."),
        Number("BuildObjectDamageRate", "Bases and Guilds", "Building damage", "ضرر المباني", "Multiplier for damage to buildings.", "مضاعف الضرر الواقع على المباني.", 0, 10, "1.000000"),
        Number("BuildObjectDeteriorationDamageRate", "Bases and Guilds", "Building deterioration", "تدهور المباني", "Multiplier for building deterioration damage.", "مضاعف ضرر تدهور المباني.", 0, 10, "1.000000"),

        Text("ServerName", "Server and Multiplayer", "Server name", "اسم الخادم", "Name shown for the Palworld server.", "اسم خادم Palworld الظاهر.", "Default Palworld Server"),
        Text("ServerDescription", "Server and Multiplayer", "Server description", "وصف الخادم", "Description shown for the Palworld server.", "وصف خادم Palworld الظاهر.", ""),
        Text("ServerPassword", "Server and Multiplayer", "Server password", "كلمة مرور الخادم", "Password required to join; blank disables it.", "كلمة المرور المطلوبة للانضمام؛ اتركها فارغة للتعطيل.", ""),
        Integer("ServerPlayerMaxNum", "Server and Multiplayer", "Maximum players", "الحد الأقصى للاعبين", "Maximum players allowed to join.", "الحد الأقصى للاعبين المسموح لهم.", 1, 32, "32"),
        Boolean("bIsPvP", "Server and Multiplayer", "Player versus player", "لاعب ضد لاعب", "Enable PvP gameplay.", "تمكين القتال بين اللاعبين.", false),
        Boolean("bShowPlayerList", "Server and Multiplayer", "Show player list", "إظهار قائمة اللاعبين", "Show the player list in the ESC menu.", "إظهار قائمة اللاعبين في قائمة ESC.", true),
        Boolean("bIsShowJoinLeftMessage", "Server and Multiplayer", "Join/leave messages", "رسائل الدخول والخروج", "Show messages when players join or leave.", "إظهار رسائل دخول وخروج اللاعبين.", true),
        List("CrossplayPlatforms", "Server and Multiplayer", "Crossplay platforms", "منصات اللعب المشترك", "Platforms allowed to connect.", "المنصات المسموح لها بالاتصال.", "(Steam,Xbox,PS5,Mac)", "Steam", "Xbox", "PS5", "Mac"),
        Boolean("bEnableVoiceChat", "Server and Multiplayer", "Voice chat", "الدردشة الصوتية", "Enable in-game voice chat.", "تمكين الدردشة الصوتية داخل اللعبة.", true),
        Boolean("bIsUseBackupSaveData", "Server and Multiplayer", "Palworld built-in backups", "نسخ Palworld الداخلية", "Enable Palworld's built-in world snapshots.", "تمكين لقطات العالم الداخلية في Palworld.", false, "Enabling built-in snapshots increases disk activity.")
    ];

    public static IReadOnlyList<string> Categories { get; } =
        Definitions.Select(item => item.Category).Distinct().ToArray();

    public static IReadOnlySet<string> ManagedNames { get; } =
        Definitions
            .Select(item => item.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> Presets { get; } =
    [
        "Official Default",
        "Casual / Fast Progress",
        "Balanced",
        "Hard",
        "Hardcore",
        "Fast Egg Hatching",
        "High Resources",
        "Custom"
    ];

    public static IReadOnlyList<PalworldWorldSettingDescriptor> Describe(
        IReadOnlyDictionary<string, string> current,
        IReadOnlyDictionary<string, string>? defaults = null) =>
        Definitions.Select(definition =>
        {
            var currentValue = current.TryGetValue(definition.Name, out var raw)
                ? ToDisplayValue(definition, raw)
                : ToDisplayValue(definition, definition.FallbackDefault);
            if (definition.Name.Equals(
                    "ServerPassword",
                    StringComparison.OrdinalIgnoreCase))
            {
                currentValue = string.IsNullOrEmpty(currentValue)
                    ? string.Empty
                    : "(configured)";
            }
            var defaultRaw = defaults is not null &&
                             defaults.TryGetValue(definition.Name, out var found)
                ? found
                : definition.FallbackDefault;
            return new PalworldWorldSettingDescriptor(
                definition.Name,
                definition.Category,
                definition.DisplayName,
                definition.ArabicDisplayName,
                definition.ValueType,
                currentValue,
                ToDisplayValue(definition, defaultRaw),
                definition.Unit,
                definition.Description,
                definition.ArabicDescription,
                definition.Minimum,
                definition.Maximum,
                definition.AllowedValues,
                true,
                definition.PerformanceWarning);
        }).ToArray();

    public static IReadOnlyDictionary<string, string> ValidateAndRender(
        IReadOnlyDictionary<string, string> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in changes)
        {
            var definition = Definitions.FirstOrDefault(item =>
                item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException(
                    $"'{name}' is not a supported Palworld setting.",
                    nameof(changes));
            result[definition.Name] = Normalize(definition, value);
        }

        return result;
    }

    public static IReadOnlyDictionary<string, string> GetPreset(
        string preset,
        IReadOnlyDictionary<string, string>? officialDefaults = null)
    {
        if (preset.Equals("Official Default", StringComparison.OrdinalIgnoreCase))
        {
            return Definitions.ToDictionary(
                item => item.Name,
                item => ToDisplayValue(
                    item,
                    officialDefaults is not null &&
                    officialDefaults.TryGetValue(item.Name, out var value)
                        ? value
                        : item.FallbackDefault),
                StringComparer.OrdinalIgnoreCase);
        }

        var values = preset.ToLowerInvariant() switch
        {
            "casual / fast progress" => new Dictionary<string, string>
            {
                ["ExpRate"] = "3",
                ["PalCaptureRate"] = "2",
                ["PalEggDefaultHatchingTime"] = "1",
                ["CollectionDropRate"] = "2",
                ["EnemyDropItemRate"] = "2"
            },
            "balanced" => new Dictionary<string, string>
            {
                ["ExpRate"] = "1.5",
                ["PalCaptureRate"] = "1.25",
                ["PalSpawnNumRate"] = "1",
                ["CollectionDropRate"] = "1.25",
                ["PalEggDefaultHatchingTime"] = "24"
            },
            "hard" => new Dictionary<string, string>
            {
                ["ExpRate"] = "0.75",
                ["PalCaptureRate"] = "0.75",
                ["PlayerDamageRateDefense"] = "1.5",
                ["DeathPenalty"] = "All"
            },
            "hardcore" => new Dictionary<string, string>
            {
                ["bHardcore"] = "true",
                ["DeathPenalty"] = "All",
                ["ExpRate"] = "0.75",
                ["PalCaptureRate"] = "0.75"
            },
            "fast egg hatching" => new Dictionary<string, string>
            {
                ["PalEggDefaultHatchingTime"] = "1"
            },
            "high resources" => new Dictionary<string, string>
            {
                ["CollectionDropRate"] = "3",
                ["EnemyDropItemRate"] = "2",
                ["MonsterFarmActionSpeedRate"] = "2",
                ["CollectionObjectRespawnSpeedRate"] = "0.5"
            },
            "custom" => new Dictionary<string, string>(),
            _ => throw new ArgumentException(
                $"Unknown Palworld settings preset '{preset}'.",
                nameof(preset))
        };
        return values;
    }

    private static string Normalize(Definition definition, string value)
    {
        value = value.Trim();
        switch (definition.ValueType)
        {
            case PalworldSettingValueType.Number:
                if (!double.TryParse(
                        value,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var number) ||
                    number < definition.Minimum ||
                    number > definition.Maximum)
                {
                    throw new ArgumentOutOfRangeException(
                        definition.Name,
                        $"Enter a value from {definition.Minimum} to {definition.Maximum} using a period as the decimal separator.");
                }

                return number.ToString("0.######", CultureInfo.InvariantCulture);

            case PalworldSettingValueType.Integer:
                if (!int.TryParse(
                        value,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var integer) ||
                    integer < definition.Minimum ||
                    integer > definition.Maximum)
                {
                    throw new ArgumentOutOfRangeException(
                        definition.Name,
                        $"Enter a whole number from {definition.Minimum} to {definition.Maximum}.");
                }

                return integer.ToString(CultureInfo.InvariantCulture);

            case PalworldSettingValueType.Boolean:
                if (!bool.TryParse(value, out var boolean))
                {
                    throw new ArgumentException(
                        $"{definition.DisplayName} must be true or false.",
                        definition.Name);
                }

                return boolean.ToString().ToLowerInvariant();

            case PalworldSettingValueType.Enumeration:
                var option = definition.AllowedValues.FirstOrDefault(item =>
                    item.Equals(value, StringComparison.OrdinalIgnoreCase));
                if (option is null)
                {
                    throw new ArgumentException(
                        $"{definition.DisplayName} must be one of: {string.Join(", ", definition.AllowedValues)}.",
                        definition.Name);
                }

                return option;

            case PalworldSettingValueType.List:
                var entries = value.Trim('(', ')')
                    .Split(',', StringSplitOptions.RemoveEmptyEntries |
                                StringSplitOptions.TrimEntries);
                if (entries.Length == 0 ||
                    entries.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                    entries.Length ||
                    entries.Any(entry => !definition.AllowedValues.Contains(
                        entry,
                        StringComparer.OrdinalIgnoreCase)))
                {
                    throw new ArgumentException(
                        $"{definition.DisplayName} contains an unsupported or duplicate platform.",
                        definition.Name);
                }

                return $"({string.Join(',', entries.Select(entry =>
                    definition.AllowedValues.First(option =>
                        option.Equals(entry, StringComparison.OrdinalIgnoreCase))))})";

            case PalworldSettingValueType.Text:
                if (value.Contains('\r') || value.Contains('\n') ||
                    value.Length > (definition.Name == "ServerDescription" ? 500 : 128))
                {
                    throw new ArgumentException(
                        $"{definition.DisplayName} is too long or contains a newline.",
                        definition.Name);
                }

                return PalworldSettingsSerializer.RenderText(value);

            default:
                throw new ArgumentOutOfRangeException(nameof(definition));
        }
    }

    private static string ToDisplayValue(Definition definition, string raw) =>
        definition.ValueType == PalworldSettingValueType.Text
            ? PalworldSettingsSerializer.ReadText(raw)
            : raw.Trim();

    private static Definition Number(
        string name,
        string category,
        string display,
        string arabic,
        string description,
        string arabicDescription,
        double minimum,
        double maximum,
        string fallback,
        string unit = "multiplier",
        string? warning = null) =>
        new(
            name,
            category,
            display,
            arabic,
            PalworldSettingValueType.Number,
            description,
            arabicDescription,
            minimum,
            maximum,
            [],
            fallback,
            unit,
            warning);

    private static Definition Integer(
        string name,
        string category,
        string display,
        string arabic,
        string description,
        string arabicDescription,
        int minimum,
        int maximum,
        string fallback,
        string unit = "count",
        string? warning = null) =>
        new(
            name,
            category,
            display,
            arabic,
            PalworldSettingValueType.Integer,
            description,
            arabicDescription,
            minimum,
            maximum,
            [],
            fallback,
            unit,
            warning);

    private static Definition Boolean(
        string name,
        string category,
        string display,
        string arabic,
        string description,
        string arabicDescription,
        bool fallback,
        string? warning = null) =>
        new(
            name,
            category,
            display,
            arabic,
            PalworldSettingValueType.Boolean,
            description,
            arabicDescription,
            null,
            null,
            ["true", "false"],
            fallback.ToString().ToLowerInvariant(),
            "on/off",
            warning);

    private static Definition Text(
        string name,
        string category,
        string display,
        string arabic,
        string description,
        string arabicDescription,
        string fallback) =>
        new(
            name,
            category,
            display,
            arabic,
            PalworldSettingValueType.Text,
            description,
            arabicDescription,
            null,
            null,
            [],
            PalworldSettingsSerializer.RenderText(fallback),
            "text",
            null);

    private static Definition Enum(
        string name,
        string category,
        string display,
        string arabic,
        string description,
        string arabicDescription,
        string fallback,
        params string[] allowed) =>
        new(
            name,
            category,
            display,
            arabic,
            PalworldSettingValueType.Enumeration,
            description,
            arabicDescription,
            null,
            null,
            allowed,
            fallback,
            "choice",
            null);

    private static Definition List(
        string name,
        string category,
        string display,
        string arabic,
        string description,
        string arabicDescription,
        string fallback,
        params string[] allowed) =>
        new(
            name,
            category,
            display,
            arabic,
            PalworldSettingValueType.List,
            description,
            arabicDescription,
            null,
            null,
            allowed,
            fallback,
            "list",
            null);

    private sealed record Definition(
        string Name,
        string Category,
        string DisplayName,
        string ArabicDisplayName,
        PalworldSettingValueType ValueType,
        string Description,
        string ArabicDescription,
        double? Minimum,
        double? Maximum,
        IReadOnlyList<string> AllowedValues,
        string FallbackDefault,
        string Unit,
        string? PerformanceWarning);
}
