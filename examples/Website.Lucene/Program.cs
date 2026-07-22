using Website.Lucene;

// Pure Lucene.NET counterpart to Website.Simple's facet search, for side by side comparison.
// The index is built on first start (10 million products by default - takes a few minutes),
// then reused. Override with LUCENE_SHOP_COUNT / LUCENE_SHOP_PATH environment variables.
var productCount = int.TryParse(Environment.GetEnvironmentVariable("LUCENE_SHOP_COUNT"), out var c) ? c : 10_000_000;
var indexPath = Environment.GetEnvironmentVariable("LUCENE_SHOP_PATH") ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "lucene.index");

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var shop = new LuceneShop(indexPath, productCount);
app.Lifetime.ApplicationStopping.Register(shop.Dispose);

app.MapGet("/", () => Results.Content(
    $"<html><body><h1>Lucene facet search example</h1><p>Index has {shop.Count:n0} products.</p>" +
    "<p><a href='/search.html'>Facet search</a></p></body></html>", "text/html; charset=utf-8"));

app.MapPost("/shop/search", (SearchRequest req) => Results.Json(shop.Search(req.Query, req.Page, req.Selections)));

app.UseDefaultFiles();
app.UseStaticFiles();
app.Run();
