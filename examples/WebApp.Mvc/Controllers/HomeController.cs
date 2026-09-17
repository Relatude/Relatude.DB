using Microsoft.AspNetCore.Mvc;
using Relatude.DB.NodeServer;

namespace WebAppMvc.Controllers;

/// <summary>
/// Controllers get the database by constructor injection: ctx.Database is the NodeStore, with
/// Query&lt;T&gt;(), Get&lt;T&gt;(id), Insert(node), Update(node) and Delete(id). One controller per area of
/// the site, one action per page; the view with the same name under Views/&lt;Controller&gt;/ renders it.
/// </summary>
public class HomeController(RelatudeDBContext ctx) : Controller {
    public IActionResult Index() {
        ViewData["Title"] = "Home";
        ViewData["Description"] = "A new website built on Relatude.DB."; // becomes <meta name="description">
        ViewData["NodeCount"] = ctx.Database.Count();
        return View();
    }

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error() {
        ViewData["Title"] = "Error";
        return View();
    }
}
