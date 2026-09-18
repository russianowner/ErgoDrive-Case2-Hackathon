using AutoFleet_Vision.Components.Models;
using AutoFleet_Vision.Components.Services;
using AutoFleet_Vision.Data;
using Microsoft.AspNetCore.Components;
using Microsoft.EntityFrameworkCore;
using Microsoft.JSInterop;
using System.Drawing;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AutoFleet_Vision.Components.Pages;

public partial class Home : ComponentBase
{
    private const int MaxVisitGapMinutes = 90;

    [Inject] private IDbContextFactory<AppDbContext> DbFactory { get; set; } = default!;
    [Inject] private TelegramBotService TelegramService { get; set; } = default!;

    private string folderPath = @"/data/Auto";
    private string selectedModel = "qwen/qwen3.8-27b";
    private bool isProcessing;
    private int totalCount;
    private int processedCount;
    private string telegramChat = string.Empty;
    private string processingMessage = string.Empty;
    private string lastRunMessage = string.Empty;
    private bool forceReprocess;
    private bool enhancedPlateOcr = true;
    private List<VehicleResult> results = [];
    private List<VehicleVisit> visits = [];

    protected override async Task OnInitializedAsync()
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        results = await db.VehicleResults.ToListAsync();
        RebuildPresentation();
    }

    private async Task ProcessDirectory()
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            await JS.InvokeVoidAsync("alert", "Укажите путь к папке.");
            return;
        }

        if (folderPath.Contains("..") ||
            folderPath.Contains("%2e%2e", StringComparison.OrdinalIgnoreCase) ||
            folderPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            await JS.InvokeVoidAsync("alert", "Обнаружена попытка обхода пути или недопустимые символы.");
            return;
        }

        string absolutePath;
        try
        {
            absolutePath = Path.GetFullPath(folderPath);
        }
        catch
        {
            await JS.InvokeVoidAsync("alert", "Указан некорректный формат пути.");
            return;
        }

        if (!Directory.Exists(absolutePath))
        {
            await JS.InvokeVoidAsync("alert", $"Папка не найдена: {absolutePath}");
            return;
        }

        var extensions = new[] { ".jpg", ".jpeg", ".png", ".webp" };
        var files = Directory.GetFiles(absolutePath)
            .Where(file => extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (files.Count == 0)
        {
            await JS.InvokeVoidAsync("alert", "В указанной папке нет изображений JPG, PNG или WEBP.");
            return;
        }

        totalCount = files.Count;
        processedCount = 0;
        isProcessing = true;
        lastRunMessage = string.Empty;
        processingMessage = "Очередь Groq: запросы выполняются по одному.";
        await InvokeAsync(StateHasChanged);

        await using var db = await DbFactory.CreateDbContextAsync();

        try
        {
            foreach (var file in files)
            {
                if (VisionService.TryGetRateLimitMessage(out string cooldownMessage))
                {
                    lastRunMessage = cooldownMessage;
                    processingMessage = cooldownMessage;
                    await InvokeAsync(StateHasChanged);
                    break;
                }

                var existingRecords = results.Where(result => result.FilePath == file).ToList();

                if (!forceReprocess &&
                    existingRecords.Count > 0 &&
                    existingRecords.All(result => result.Status == "Успешно"))
                {
                    processedCount++;
                    continue;
                }

                processingMessage = enhancedPlateOcr
                    ? $"Готовим детализацию номера: {Path.GetFileName(file)}"
                    : $"Анализируем: {Path.GetFileName(file)}";
                await InvokeAsync(StateHasChanged);

                var detectedVehicles = await VisionService.AnalyzeImageAsync(
                    file,
                    selectedModel,
                    UpdateProcessingMessageAsync,
                    enhancedPlateOcr);

                if (detectedVehicles.Any(IsGroqRateLimitError) ||
                    VisionService.TryGetRateLimitMessage(out cooldownMessage))
                {
                    lastRunMessage = string.IsNullOrWhiteSpace(cooldownMessage)
                        ? "Обработка временно приостановлена."
                        : cooldownMessage;
                    processingMessage = lastRunMessage;
                    await InvokeAsync(StateHasChanged);
                    break;
                }

                if (existingRecords.Count > 0)
                {
                    db.VehicleResults.RemoveRange(existingRecords);
                    results.RemoveAll(result => existingRecords.Contains(result));
                }
                try
                {
                    string processedDir = Path.Combine(absolutePath, "Processed");
                    if (!Directory.Exists(processedDir)) Directory.CreateDirectory(processedDir);

                    using (var img = System.Drawing.Image.FromFile(file))
                    using (var g = Graphics.FromImage(img))
                    {
                        foreach (var res in detectedVehicles)
                        {
                            if (res.HasTechnique && res.PlateBbox != null && res.PlateBbox.Length == 4 && res.PlateBbox.Sum() > 0)
                            {
                                float bx = res.PlateBbox[0];
                                float by = res.PlateBbox[1];
                                float bw = res.PlateBbox[2];
                                float bh = res.PlateBbox[3];
                                int x = (int)(bx > 1.0f ? bx : bx * img.Width);
                                int y = (int)(by > 1.0f ? by : by * img.Height);
                                int w = (int)(bw > 1.0f ? bw : bw * img.Width);
                                int h = (int)(bh > 1.0f ? bh : bh * img.Height);
                                w = Math.Min(w, img.Width - x);
                                h = Math.Min(h, img.Height - y);

                                if (w > 0 && h > 0)
                                {
                                    using var pen = new Pen(System.Drawing.Color.LimeGreen, 3);
                                    g.DrawRectangle(pen, x, y, w, h);

                                    string plateText = string.IsNullOrWhiteSpace(res.PlateNumber) ? "Не читается" : res.PlateNumber;
                                    using var brush = new SolidBrush(System.Drawing.Color.FromArgb(180, System.Drawing.Color.LimeGreen));
                                    g.FillRectangle(brush, x, y + h + 3, w, 22);
                                    g.DrawString(plateText, new Font("Arial", 12, FontStyle.Bold), Brushes.White, new PointF(x, y + h + 4));
                                }
                            }
                        }
                        img.Save(Path.Combine(processedDir, Path.GetFileName(file)));
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка отрисовки: {ex.Message}");
                }

                db.VehicleResults.AddRange(detectedVehicles);
                results.AddRange(detectedVehicles);
                if (!string.IsNullOrWhiteSpace(telegramChat))
                {
                    foreach (var res in detectedVehicles)
                    {
                        _ = TelegramService.SendVehicleAlertAsync(res, telegramChat);
                    }
                }

                await db.SaveChangesAsync();
                processedCount++;
                RebuildPresentation();
                await InvokeAsync(StateHasChanged);
            }
            if (!string.IsNullOrWhiteSpace(telegramChat) && results.Any())
            {
                processingMessage = "Отправка отчетов в Telegram...";
                await InvokeAsync(StateHasChanged);

                string jsonContent = GenerateJsonString();
                string csvContent = GenerateCsvString();

                await TelegramService.SendDocumentAsync(telegramChat, "checkpoint-results.json", jsonContent);
                await TelegramService.SendDocumentAsync(telegramChat, "checkpoint-results.csv", csvContent);
            }
        }
        finally
        {
            isProcessing = false;
            if (string.IsNullOrWhiteSpace(lastRunMessage))
            {
                lastRunMessage = $"Обработано кадров: {processedCount} из {totalCount}.";
            }
            processingMessage = string.Empty;
            await InvokeAsync(StateHasChanged);
        }
    }

    private async Task ClearDatabase()
    {
        await using var db = await DbFactory.CreateDbContextAsync();
        db.VehicleResults.RemoveRange(db.VehicleResults);
        await db.SaveChangesAsync();

        results.Clear();
        visits.Clear();
        await InvokeAsync(StateHasChanged);
    }

    private async Task UpdateProcessingMessageAsync(string message)
    {
        processingMessage = message;
        await InvokeAsync(StateHasChanged);
    }

    private void RebuildPresentation()
    {
        results = results
            .OrderByDescending(result => result.CapturedAt ?? DateTime.MinValue)
            .ThenBy(result => result.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        visits = BuildVisits(results)
            .OrderByDescending(visit => visit.StartedAt)
            .ToList();
    }
    private static bool IsGroqRateLimitError(VehicleResult result) =>
        result.Status.Contains("TooManyRequests", StringComparison.OrdinalIgnoreCase) ||
        result.Status.Contains("429", StringComparison.OrdinalIgnoreCase) ||
        result.Status.Contains("Лимит Groq", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<VehicleVisit> BuildVisits(IEnumerable<VehicleResult> source)
    {
        var groups = source
            .Where(IsEligibleForVisit)
            .GroupBy(result => NormalizePlate(result.PlateNumber), StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var ordered = group
                .OrderBy(result => result.CapturedAt)
                .ThenBy(result => result.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var segment = new List<VehicleResult>();
            int sequence = 1;

            foreach (var current in ordered)
            {
                if (segment.Count > 0 && !CanJoinVisit(segment[^1], current))
                {
                    yield return CreateVisit(group.Key, segment, sequence++);
                    segment = [];
                }

                segment.Add(current);
            }

            if (segment.Count > 0)
            {
                yield return CreateVisit(group.Key, segment, sequence);
            }
        }
    }

    private static VehicleVisit CreateVisit(
        string plateKey,
        IReadOnlyList<VehicleResult> events,
        int sequence)
    {
        var first = events[0];
        var last = events[^1];
        bool completed = events.Count > 1 && last.CapturedAt > first.CapturedAt;
        int? firstLoad = events.FirstOrDefault(IsReliableLoad)?.LoadPercentage;
        int? lastLoad = events.LastOrDefault(IsReliableLoad)?.LoadPercentage;
        string loadChange = FormatLoadChange(firstLoad, lastLoad, last.LoadStatus);
        string riskStatus = GetRiskStatus(completed, firstLoad, lastLoad);

        return new VehicleVisit
        {
            VisitId = $"{plateKey}-{first.CapturedAt:yyyyMMddHHmmss}-{sequence:D2}",
            PlateNumber = first.PlateNumber,
            VehicleName = FormatVehicleName(last),
            StartedAt = first.CapturedAt!.Value,
            EndedAt = completed ? last.CapturedAt : null,
            Duration = completed ? last.CapturedAt!.Value - first.CapturedAt!.Value : null,
            EventCount = events.Count,
            LoadChange = loadChange,
            State = completed ? "Завершён" : "Ожидает второй кадр",
            MatchBasis = "Номер совпадает (≥ 90%), интервал ≤ 90 мин", 
            RiskStatus = riskStatus
        };
    }

    private string GenerateJsonString()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };
        return JsonSerializer.Serialize(results, options);
    }

    private string GenerateCsvString()
    {
        var builder = new StringBuilder();
        builder.AppendLine("FileName;CapturedAt;CameraId;CheckpointStatus;HasTechnique;VehicleType;TransportUnit;Make;Model;CabColor;BodyColor;AxleConfiguration;PlateNumber;PlateCountry;PlateReadStatus;PlateConfidence;LoadVisibility;LoadStatus;LoadPercentage;LoadConfidence;ProcessingTimeMs;Status");

        foreach (var result in results)
        {
            builder.AppendLine(string.Join(';', new[]
            {
                Csv(result.FileName),
                Csv(result.CapturedAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? string.Empty),
                Csv(result.CameraId),
                Csv(result.CheckpointStatus),
                Csv(result.HasTechnique.ToString()),
                Csv(result.VehicleType),
                Csv(result.TransportUnit),
                Csv(result.Make),
                Csv(result.Model),
                Csv(result.CabColor),
                Csv(result.BodyColor),
                Csv(result.AxleConfiguration),
                Csv(result.PlateNumber),
                Csv(result.PlateCountry),
                Csv(result.PlateReadStatus),
                result.PlateConfidence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Csv(result.LoadVisibility),
                Csv(result.LoadStatus),
                Csv(result.LoadPercentage?.ToString() ?? string.Empty),
                result.LoadConfidence.ToString(System.Globalization.CultureInfo.InvariantCulture),
                result.ProcessingTimeMs.ToString(),
                Csv(result.Status)
            }));
        }
        return builder.ToString();
    }

    private async Task ExportJson()
    {
        var json = GenerateJsonString();
        await DownloadFile("checkpoint-results.json", json, "application/json");
    }

    private async Task ExportCsv()
    {
        var csv = GenerateCsvString();
        await DownloadFile("checkpoint-results.csv", csv, "text/csv;charset=utf-8;");
    }

    private async Task DownloadFile(string fileName, string content, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var base64 = Convert.ToBase64String(bytes);
        await JS.InvokeVoidAsync("eval", $@"
            const a = document.createElement('a');
            a.href = 'data:{contentType};base64,{base64}';
            a.download = '{fileName}';
            a.click();
        ");
    }

    private static bool IsEligibleForVisit(VehicleResult result) =>
            result.Status == "Успешно" &&
            result.HasTechnique &&
            result.CapturedAt.HasValue &&
            result.PlateReadStatus.Equals("Надёжно", StringComparison.OrdinalIgnoreCase) &&
            result.PlateConfidence >= 0.90 &&
            IsActualPlate(result.PlateNumber);

    private static bool CanJoinVisit(VehicleResult previous, VehicleResult current)
    {
        if (!previous.CapturedAt.HasValue || !current.CapturedAt.HasValue)
        {
            return false;
        }

        TimeSpan gap = current.CapturedAt.Value - previous.CapturedAt.Value;

        return gap >= TimeSpan.Zero &&
               gap <= TimeSpan.FromMinutes(MaxVisitGapMinutes);
    }

    private static bool HasCompatibleProfile(VehicleResult left, VehicleResult right) =>
        SameOrUnknown(left.VehicleType, right.VehicleType, VehicleClass) &&
        SameOrUnknown(left.Make, right.Make) &&
        SameOrUnknown(left.TransportUnit, right.TransportUnit, VehicleClass) &&
        SameOrUnknown(left.CabColor, right.CabColor) &&
        SameOrUnknown(left.BodyColor, right.BodyColor) &&
        SameOrUnknown(left.AxleConfiguration, right.AxleConfiguration);

    private static bool SameOrUnknown(
        string? left,
        string? right,
        Func<string, string>? normalize = null)
    {
        if (!IsKnown(left) || !IsKnown(right))
        {
            return true;
        }

        string leftValue = normalize is null ? NormalizeProfileValue(left!) : normalize(left!);
        string rightValue = normalize is null ? NormalizeProfileValue(right!) : normalize(right!);
        return leftValue == rightValue;
    }

    private static string VehicleClass(string value)
    {
        string normalized = NormalizeProfileValue(value);
        if (normalized.Contains("трактор")) return "трактор";
        if (normalized.Contains("погруз")) return "погрузчик";
        if (normalized.Contains("самосвал")) return "самосвал";
        if (normalized.Contains("тягач")) return "тягач";
        if (normalized.Contains("груз")) return "грузовой";
        return normalized;
    }

    private static bool IsKnown(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        !value.Contains("не определ", StringComparison.OrdinalIgnoreCase) &&
        !value.Contains("не видно", StringComparison.OrdinalIgnoreCase) &&
        !value.Equals("нет", StringComparison.OrdinalIgnoreCase);

    private static bool IsActualPlate(string? plateNumber) =>
        !string.IsNullOrWhiteSpace(plateNumber) &&
        !plateNumber.Contains("не чит", StringComparison.OrdinalIgnoreCase) &&
        !plateNumber.Contains("отсутств", StringComparison.OrdinalIgnoreCase) &&
        !plateNumber.Contains("не определ", StringComparison.OrdinalIgnoreCase);

    private static string NormalizePlate(string plate) =>
        new string(plate.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    private static string NormalizeProfileValue(string value) =>
        string.Join(' ', value.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string FormatVehicleName(VehicleResult result)
    {
        var parts = new[] { result.Make, result.Model }
            .Where(value => !string.IsNullOrWhiteSpace(value) &&
                            !value.Equals("Не определено", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (parts.Count > 0)
        {
            return string.Join(' ', parts);
        }

        return IsKnown(result.TransportUnit) ? result.TransportUnit : result.VehicleType;
    }

    private static string FormatLoadChange(int? firstLoad, int? lastLoad, string lastLoadStatus)
    {
        if (firstLoad.HasValue && lastLoad.HasValue)
        {
            return $"{firstLoad}% → {lastLoad}%";
        }

        if (lastLoad.HasValue)
        {
            return $"{lastLoad}% ({lastLoadStatus})";
        }

        return "Загрузка не подтверждена камерой";
    }

    private static string FormatCapturedAt(DateTime? capturedAt) =>
        capturedAt?.ToString("dd.MM.yyyy HH:mm:ss") ?? "Не распознано";

    private static string FormatDuration(TimeSpan? duration) =>
        duration is null
            ? "Ожидает второй кадр"
            : duration.Value.TotalHours >= 1
                ? $"{(int)duration.Value.TotalHours:00}:{duration.Value.Minutes:00}:{duration.Value.Seconds:00}"
                : $"{duration.Value.Minutes:00}:{duration.Value.Seconds:00}";

    private static string FormatLoad(VehicleResult result) =>
        !result.LoadVisibility.Equals("Содержимое кузова видно", StringComparison.OrdinalIgnoreCase)
            ? "Содержимое кузова не видно"
            : IsReliableLoad(result)
                ? $"{result.LoadStatus} · {result.LoadPercentage}%"
                : "Оценка неуверенная";

    private static bool IsReliableLoad(VehicleResult result) =>
        result.LoadVisibility.Equals("Содержимое кузова видно", StringComparison.OrdinalIgnoreCase) &&
        result.LoadConfidence >= 0.70 &&
        result.LoadPercentage.HasValue;

    private static string FormatPlate(VehicleResult result) =>
        result.PlateReadStatus.Equals("Надёжно", StringComparison.OrdinalIgnoreCase) &&
        result.PlateConfidence >= 0.90 &&
        IsActualPlate(result.PlateNumber)
            ? result.PlateNumber
            : "Не читается";

    private static string FormatPlateStatus(VehicleResult result) =>
        result.PlateReadStatus.Equals("Надёжно", StringComparison.OrdinalIgnoreCase) &&
        result.PlateConfidence >= 0.90 &&
        IsActualPlate(result.PlateNumber)
            ? $"Надёжно · {result.PlateConfidence:P0}"
            : "Не читается";

    private static string PlateClass(VehicleResult result) =>
        result.PlateReadStatus.Equals("Надёжно", StringComparison.OrdinalIgnoreCase) &&
        result.PlateConfidence >= 0.90 &&
        IsActualPlate(result.PlateNumber)
            ? "reliable"
            : "unreliable";

    private static string FormatProfile(VehicleResult result)
    {
        var parts = new[] { result.TransportUnit, result.CabColor, result.BodyColor, result.AxleConfiguration }
            .Where(IsKnown)
            .ToList();

        return parts.Count > 0 ? string.Join(" · ", parts) : "Профиль не определён";
    }

    private static string GetRiskStatus(bool completed, int? firstLoad, int? lastLoad)
    {
        if (firstLoad.HasValue && lastLoad.HasValue && firstLoad.Value - lastLoad.Value >= 25)
        {
            return "Проверить: загрузка снизилась ≥ 25 п.п.";
        }

        if (completed && (!firstLoad.HasValue || !lastLoad.HasValue))
        {
            return "Нет визуальных данных о загрузке";
        }

        return "Нет оснований для сигнала";
    }

    private static string RiskClass(string riskStatus) => riskStatus.StartsWith("Проверить", StringComparison.Ordinal)
        ? "attention"
        : riskStatus.StartsWith("Нет визуальных", StringComparison.Ordinal)
            ? "unknown"
            : "safe";

    private static string LoadClass(string loadStatus) => loadStatus switch
    {
        "Пустой" => "empty",
        "Частично загружен" => "partial",
        "Загружен" => "loaded",
        _ => "unknown"
    };

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";
}
