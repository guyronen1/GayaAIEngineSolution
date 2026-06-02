using AIEngineAPI.Extensions;
using MaiaAI.Infrastructure.Extensions;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ── Logging: Serilog (replaces the default Console+Debug providers) ────────
// Config lives in appsettings.json under "Serilog" — both sinks (console
// for `dotnet run` visibility + rolling daily file at logs/maia-api-.log
// with 30-day retention) are declarative there, so ops can swap rolling
// policy or add a network sink without a rebuild.
builder.Host.UseSerilog((ctx, services, cfg) =>
    cfg.ReadFrom.Configuration(ctx.Configuration)
       .ReadFrom.Services(services)
       .Enrich.FromLogContext());

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowLocalhost", policy =>
    {
        policy.WithOrigins(
                "http://localhost:3000",
                "http://localhost:5173",
                "http://localhost:4200",
                "http://127.0.0.1:5095"
            )
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});

builder.Services.AddControllers()
    .AddJsonOptions(opts =>
        opts.JsonSerializerOptions.ReferenceHandler =
            System.Text.Json.Serialization.ReferenceHandler.IgnoreCycles);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ── HTTP client for ApiCallExecutor ─────────────────────────────────────────
builder.Services.AddHttpClient("FixEngine");

// ── Infrastructure: DB, repos, strategies, parsers, workers ─────────────────
builder.Services.AddMaiaAI(
    builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' is missing."));

// ── Application: use cases (registered as interfaces for testability) ────────
builder.Services.AddApplicationServices();

// ── Global error handling ─────────────────────────────────────────────────────
builder.Services.AddGlobalExceptionHandling();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseExceptionHandler();
app.UseCors("AllowLocalhost");
app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

app.Run();
