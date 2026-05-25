using AIEngineAPI.Extensions;
using MaiaAI.Infrastructure.Extensions;

var builder = WebApplication.CreateBuilder(args);

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
