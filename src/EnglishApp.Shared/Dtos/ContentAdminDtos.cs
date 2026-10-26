using EnglishApp.Shared.Enums;

namespace EnglishApp.Shared.Dtos;

public class AdminScenarioDto
{
    public int Id { get; set; }
    public string Slug { get; set; } = "";
    public string TitleTr { get; set; } = "";
    public string TitleEn { get; set; } = "";
    public string? DescriptionTr { get; set; }
    public CefrLevel Level { get; set; }
    public byte CategoryId { get; set; }
    public string CategoryName { get; set; } = "";
    public ContentStatus Status { get; set; }
    public bool IsAiGenerated { get; set; }
    public int WordCount { get; set; }
    public int LineCount { get; set; }
    public int MissingAudio { get; set; }
    public bool HasQuiz { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? PublishedAt { get; set; }
}

public class AdminScenarioDetailDto : AdminScenarioDto
{
    public List<WordDto> Words { get; set; } = [];
    public List<DialogueLineDto> Lines { get; set; } = [];
}

public class ScenarioCategoryDto
{
    public byte Id { get; set; }
    public string NameTr { get; set; } = "";
}

public class GenerateScenarioRequest
{
    public byte CategoryId { get; set; }
    public CefrLevel Level { get; set; } = CefrLevel.A2;
    public string Topic { get; set; } = "";
    public string? Notes { get; set; }
}

public class WordImportRequest
{
    /// <summary>Her satıra bir kelime, sıklık sırasıyla (NGSL vb.). "1 the" veya "the" formatı.</summary>
    public string Text { get; set; } = "";
    public int? StartRank { get; set; }
}

public class WordImportResultDto
{
    public int Added { get; set; }
    public int Skipped { get; set; }
}

public class WordPoolStatsDto
{
    public int TotalShared { get; set; }
    public int GeneralPool { get; set; }
    public int NeedsEnrichment { get; set; }
    public int MissingAudio { get; set; }
}

public class EnrichResultDto
{
    public int Processed { get; set; }
    public int Remaining { get; set; }
    public int Failed { get; set; }
}

public class AudioManifestItemDto
{
    public string Voice { get; set; } = "";
    public string Text { get; set; } = "";
    public string Lang { get; set; } = "en-us";
    public string File { get; set; } = "";
}
