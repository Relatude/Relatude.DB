using Relatude.DB.FileConversion;

var builder = WebApplication.CreateBuilder(args);

builder.AddRelatudeDB(options => {
    options.FileConverters.Add(new SkiaImageConverter());
    options.FileConverters.Add(new FFMpegVideoConverter());
});

var app = builder.Build();

app.UseRelatudeDB();

app.UseHttpsRedirection();

// The client (../../Client) builds its output into wwwroot:
app.UseDefaultFiles();
app.UseStaticFiles();

// Minimal API the client calls. In development Vite proxies /api to this server.
app.MapGet("/api/hello", () => new HelloResponse("Hello from Relatude.DB!", DateTime.UtcNow));

// Any unmatched route is handled by the SPA:
app.MapFallbackToFile("index.html");

app.Run();

record HelloResponse(string Message, DateTime ServerTimeUtc);
