using AutoFleet_Vision.Components;
using AutoFleet_Vision.Components.Services;
using AutoFleet_Vision.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlite("Data Source=autofleet.db"));

builder.Services.AddHttpClient<GroqVisionService>();
builder.Services.AddHttpClient<TelegramBotService>();

var app = builder.Build();

await using (var scope = app.Services.CreateAsyncScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    await using var db = await dbFactory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();
    await EnsureVehicleResultsColumnsAsync(db);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static async Task EnsureVehicleResultsColumnsAsync(AppDbContext db)
{
    var expectedColumns = new Dictionary<string, string>
    {
        ["CapturedAt"] = "TEXT NULL",
        ["CameraId"] = "TEXT NOT NULL DEFAULT 'Не определена'",
        ["CheckpointStatus"] = "TEXT NOT NULL DEFAULT 'Не определено'",
        ["LoadStatus"] = "TEXT NOT NULL DEFAULT 'Не определено'",
        ["LoadPercentage"] = "INTEGER NULL",
        ["PlateReadStatus"] = "TEXT NOT NULL DEFAULT 'Не читается'",
        ["PlateConfidence"] = "REAL NOT NULL DEFAULT 0",
        ["TransportUnit"] = "TEXT NOT NULL DEFAULT 'Не определено'",
        ["CabColor"] = "TEXT NOT NULL DEFAULT 'Не определено'",
        ["BodyColor"] = "TEXT NOT NULL DEFAULT 'Не определено'",
        ["AxleConfiguration"] = "TEXT NOT NULL DEFAULT 'Не определено'",
        ["LoadVisibility"] = "TEXT NOT NULL DEFAULT 'Не видно содержимое кузова'",
        ["LoadConfidence"] = "REAL NOT NULL DEFAULT 0"
    };

    var connection = db.Database.GetDbConnection();
    await connection.OpenAsync();

    try
    {
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(\"VehicleResults\");";
            await using var reader = await command.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                existingColumns.Add(reader.GetString(1));
            }
        }

        foreach (var (name, definition) in expectedColumns)
        {
            if (existingColumns.Contains(name))
            {
                continue;
            }

            await using var command = connection.CreateCommand();
            command.CommandText = $"ALTER TABLE \"VehicleResults\" ADD COLUMN \"{name}\" {definition};";
            await command.ExecuteNonQueryAsync();
        }
    }
    finally
    {
        await connection.CloseAsync();
    }
}
