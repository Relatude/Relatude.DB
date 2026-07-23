using Website.Sqlite;

// Pure SQLite counterpart to Website.Simple's and Website.Lucene's facet search, for side by side
// comparison. The database is built on first start (10 million products by default - takes a few
// minutes), then reused. Override with SQLITE_SHOP_COUNT / SQLITE_SHOP_PATH environment variables.
var productCount = int.TryParse(Environment.GetEnvironmentVariable("SQLITE_SHOP_COUNT"), out var c) ? c : 10_000_000;
var dbPath = Environment.GetEnvironmentVariable("SQLITE_SHOP_PATH") ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "shop.sqlite");

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var shop = new SqliteShop(dbPath, productCount);
app.Lifetime.ApplicationStopping.Register(shop.Dispose);

app.MapGet("/", () => Results.Content(
    $"<html><body><h1>SQLite facet search example</h1><p>Database has {shop.Count:n0} products.</p>" +
    "<p><a href='/search.html'>Facet search</a></p></body></html>", "text/html; charset=utf-8"));

app.MapPost("/shop/search", (SearchRequest req) => Results.Json(shop.Search(req.Query, req.Page, req.Selections)));

app.UseDefaultFiles();
app.UseStaticFiles();
app.Run();
