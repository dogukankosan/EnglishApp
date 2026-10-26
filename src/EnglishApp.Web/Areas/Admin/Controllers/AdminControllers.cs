using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EnglishApp.Web.Areas.Admin.Controllers;

// UI tarafındaki rol kontrolü sadece kolaylık için; asıl yetki kontrolü API'de.

[Area("Admin")]
[Authorize(Roles = "Admin")]
public sealed class UsersController : Controller
{
    public IActionResult Index() => View();
}

[Area("Admin")]
[Authorize(Roles = "Admin")]
public sealed class SettingsController : Controller
{
    public IActionResult Index() => View();
}

[Area("Admin")]
[Authorize(Roles = "Admin")]
public sealed class LogsController : Controller
{
    public IActionResult Index() => View();
}

[Area("Admin")]
[Authorize(Roles = "Admin")]
public sealed class ContentController : Controller
{
    public IActionResult Index() => View();
}
