using Website.Elastic;

// Pure Elasticsearch counterpart to Website.Simple's / Website.Lucene's / Website.Sqlite's facet
// search, for side by side comparison. Elasticsearch is a separate service, so this app connects to
// a cluster (default http://localhost:9200) rather than embedding a store. The index is built on
// first start (10 million products by default - takes a few minutes), then reused.
//
// Override with environment variables:
//   ELASTIC_SHOP_URL    cluster URL           (default http://localhost:9200)
//   ELASTIC_SHOP_INDEX  index name            (default shop)
//   ELASTIC_SHOP_COUNT  product count to seed (default 10,000,000)
//   ELASTIC_SHOP_USER / ELASTIC_SHOP_PASSWORD  basic auth (optional; needed for a secured cluster)
//
// A quick local cluster for trying this out (no auth, no TLS):
//   docker run -p 9200:9200 -e discovery.type=single-node -e xpack.security.enabled=false \
//       docker.elastic.co/elasticsearch/elasticsearch:8.15.0
var url = Environment.GetEnvironmentVariable("ELASTIC_SHOP_URL") ?? "http://localhost:9200";
var index = Environment.GetEnvironmentVariable("ELASTIC_SHOP_INDEX") ?? "shop";
var productCount = int.TryParse(Environment.GetEnvironmentVariable("ELASTIC_SHOP_COUNT"), out var c) ? c : 10_000_000;
var user = Environment.GetEnvironmentVariable("ELASTIC_SHOP_USER");
var password = Environment.GetEnvironmentVariable("ELASTIC_SHOP_PASSWORD");

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var shop = new ElasticShop(url, index, productCount, user, password);
app.Lifetime.ApplicationStopping.Register(shop.Dispose);

app.MapGet("/", () => Results.Content(
    $"<html><body><h1>Elasticsearch facet search example</h1><p>Index '{index}' has {shop.Count:n0} products.</p>" +
    "<p><a href='/search.html'>Facet search</a></p></body></html>", "text/html; charset=utf-8"));

app.MapPost("/shop/search", (SearchRequest req) => Results.Json(shop.Search(req.Query, req.Page, req.Selections)));

app.UseDefaultFiles();
app.UseStaticFiles();
app.Run();
