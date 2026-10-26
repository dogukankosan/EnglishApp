using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EnglishApp.Web.Controllers;

// Sayfalar sadece iskelet; veriler AJAX ile /bff üzerinden API'den gelir.

[Authorize]
public sealed class HomeController : Controller
{
    public IActionResult Index() => View();
}

[Authorize]
public sealed class StudyController : Controller
{
    /// <summary>Senaryo listesi + bugünkü çalışmaya başla.</summary>
    public IActionResult Index() => View();

    /// <summary>Kart çalışma ekranı. scenarioId boşsa tüm senaryolardan.</summary>
    public IActionResult Session(int? scenarioId)
    {
        ViewBag.ScenarioId = scenarioId;
        return View();
    }

    /// <summary>Senaryo diyaloğu: dinle, oku, kelimeleri gör.</summary>
    public IActionResult Scenario(int id)
    {
        ViewBag.ScenarioId = id;
        return View();
    }
}

[Authorize]
public sealed class WordsController : Controller
{
    public IActionResult Index(string? filter) => View(model: filter);
}

[Authorize]
public sealed class PlacementController : Controller
{
    public IActionResult Index() => View();
}

[Authorize]
public sealed class PracticeController : Controller
{
    /// <summary>AI pratik merkezi: konuşma, yazma, geçmiş.</summary>
    public IActionResult Index() => View();

    /// <summary>/Practice/Talk/5 (devam) · /Practice/Talk?scenarioId=2 (yeni rol yapma) · /Practice/Talk?mode=free</summary>
    public IActionResult Talk(long? id, int? scenarioId, string? mode)
    {
        ViewBag.ConversationId = id;
        ViewBag.ScenarioId = scenarioId;
        ViewBag.Mode = mode == "free" ? "FreeTalk" : "RolePlay";
        return View();
    }

    public IActionResult Write(long? id)
    {
        ViewBag.WritingId = id;
        return View();
    }

    /// <summary>/Practice/Listen (liste) · /Practice/Listen/3 (o senaryonun dinleme testi)</summary>
    public IActionResult Listen(int? id)
    {
        ViewBag.ScenarioId = id;
        return View();
    }

    public IActionResult Capture() => View();

    /// <summary>Dikte: dinle, yaz, kontrol et. ?weak=1 sadece zayıf cümleler.</summary>
    public IActionResult Dictation(int? scenarioId)
    {
        ViewBag.ScenarioId = scenarioId;
        return View();
    }

    /// <summary>/Practice/Reading (yeni metin + liste) · /Practice/Reading/5 (metni oku)</summary>
    public IActionResult Reading(long? id)
    {
        ViewBag.ReadingId = id;
        return View();
    }

    /// <summary>Dizi / YouTube / podcast önerileri.</summary>
    public IActionResult Media() => View();

    /// <summary>/Practice/Video (ekle + liste) · /Practice/Video/5 (sahne sahne izle)</summary>
    public IActionResult Video(long? id)
    {
        ViewBag.VideoId = id;
        return View();
    }

    /// <summary>/Practice/Travel (yeni kit + liste) · /Practice/Travel/5 (kit detayı)</summary>
    public IActionResult Travel(long? id)
    {
        ViewBag.KitId = id;
        return View();
    }
}

/// <summary>Sıralama: tüm kullanıcıların özet durumu (veri AJAX ile API'den).</summary>
[Authorize]
public sealed class LeaderboardController : Controller
{
    public IActionResult Index() => View();
}

[Authorize]
public sealed class ProfileController : Controller
{
    public IActionResult Index() => View();
}
