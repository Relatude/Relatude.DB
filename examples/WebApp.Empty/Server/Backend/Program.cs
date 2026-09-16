using Relatude.DB.FileConversion;

var builder = WebApplication.CreateBuilder(args);
builder.AddRelatudeDB(options => {
    options.FileConverters.Add(new SkiaImageConverter());
    options.FileConverters.Add(new FFMpegVideoConverter());
});

var app = builder.Build();
app.UseRelatudeDB();
app.UseHttpsRedirection();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/api/hello", () => new { Message = "Hello from Relatude.DB!", Utc = DateTime.UtcNow });
app.Run();
