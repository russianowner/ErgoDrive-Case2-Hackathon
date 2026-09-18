using AutoFleet_Vision.Components.Models;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AutoFleet_Vision.Components.Services;

public sealed class GroqVisionService
{
    private const int MaxVehiclesPerImage = 1;
    private const int FullFrameMaxWidth = 1280;
    private const int DetailImageMaxWidth = 1024;

    private static readonly SemaphoreSlim RequestGate = new(1, 1);
    private static readonly object RateLimitLock = new();
    private static DateTimeOffset? rateLimitUntilUtc;
    private static readonly Regex RetryAfterInBody = new(
        @"try again in\s+(?<seconds>\d+(?:\.\d+)?)s",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    public GroqVisionService(HttpClient httpClient, IConfiguration config)
    {
        _httpClient = httpClient;
        _apiKey = config["Groq:ApiKey"] ?? string.Empty;
    }

    public bool IsRateLimitActive => TryGetRateLimitMessage(out _);

    public bool TryGetRateLimitMessage(out string message)
    {
        DateTimeOffset? until;
        lock (RateLimitLock)
        {
            until = rateLimitUntilUtc;
            if (until is not null && until <= DateTimeOffset.UtcNow)
            {
                rateLimitUntilUtc = null;
                until = null;
            }
        }

        if (until is not null)
        {
            message = "Обработка временно приостановлена.";
            return true;
        }

        message = string.Empty;
        return false;
    }

    public async Task<List<VehicleResult>> AnalyzeImageAsync(
        string filePath,
        string model = "qwen/qwen3.8-27b",
        Func<string, Task>? statusChanged = null,
        bool enhancedPlateOcr = true,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                return CreateFailure(filePath, "Не задан Groq:ApiKey.", stopwatch);
            }

            if (TryGetRateLimitMessage(out string cooldownMessage))
            {
                return CreateFailure(filePath, $"Ошибка API: TooManyRequests - {cooldownMessage}", stopwatch);
            }

            byte[] imageBytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
            var visionImages = CreateVisionImages(filePath, imageBytes, enhancedPlateOcr);
            object requestBody = CreateRequestBody(model, visionImages);

            string responseString;
            HttpStatusCode statusCode;
            bool succeeded;

            await RequestGate.WaitAsync(cancellationToken);
            try
            {
                (succeeded, statusCode, responseString) = await SendWithRetryAsync(
                    requestBody,
                    statusChanged,
                    cancellationToken);

                if (!succeeded && visionImages.Count > 1 && IsRequestTooLarge(responseString))
                {
                    if (statusChanged is not null)
                    {
                        await statusChanged("Увеличенные фрагменты не поместились в запрос — анализируем полный кадр.");
                    }

                    (succeeded, statusCode, responseString) = await SendWithRetryAsync(
                        CreateRequestBody(model, [visionImages[0]]),
                        statusChanged,
                        cancellationToken);
                }
            }
            finally
            {
                RequestGate.Release();
            }

            if (!succeeded)
            {
                return CreateFailure(
                    filePath,
                    $"Ошибка API: {statusCode} - {responseString}",
                    stopwatch);
            }

            return ParseResults(filePath, responseString, stopwatch);
        }
        catch (OperationCanceledException)
        {
            return CreateFailure(filePath, "Обработка отменена.", stopwatch);
        }
        catch (Exception ex)
        {
            return CreateFailure(filePath, $"Исключение: {ex.Message}", stopwatch);
        }
        finally
        {
            stopwatch.Stop();
        }
    }

    private async Task<(bool Succeeded, HttpStatusCode StatusCode, string ResponseString)> SendWithRetryAsync(
        object requestBody,
        Func<string, Task>? statusChanged,
        CancellationToken cancellationToken)
    {
        string responseString = string.Empty;
        HttpStatusCode statusCode = HttpStatusCode.OK;

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "https://api.groq.com/openai/v1/chat/completions");

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(requestBody),
            Encoding.UTF8,
            "application/json");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        statusCode = response.StatusCode;
        responseString = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return (true, statusCode, responseString);
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            TimeSpan retryDelay = GetRetryDelay(response, responseString);
            if (retryDelay < TimeSpan.FromSeconds(60))
            {
                retryDelay = TimeSpan.FromMinutes(5);
            }

            DateTimeOffset until = DateTimeOffset.UtcNow.Add(retryDelay);
            lock (RateLimitLock)
            {
                if (rateLimitUntilUtc is not { } known || until > known)
                {
                    rateLimitUntilUtc = until;
                }
            }

            responseString = "Лимит Groq. Обработка временно приостановлена.";
            if (statusChanged is not null)
            {
                await statusChanged("Обработка временно приостановлена.");
            }
        }

        return (false, statusCode, responseString);
    }

    private static List<VehicleResult> ParseResults(
        string filePath,
        string responseString,
        System.Diagnostics.Stopwatch stopwatch)
    {
        try
        {
            using var responseDocument = JsonDocument.Parse(responseString);
            string content = responseDocument.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;

            string json = ExtractJson(content);
            var vehicles = DeserializeVehicles(json);

            if (vehicles.Count == 0)
            {
                return
                [
                    CreateVehicleResult(
                        new VehicleResult { HasTechnique = false, VehicleType = "Нет", Confidence = 0 },
                        filePath,
                        stopwatch.ElapsedMilliseconds)
                ];
            }

            return vehicles
                .Take(MaxVehiclesPerImage)
                .Select(vehicle => CreateVehicleResult(vehicle, filePath, stopwatch.ElapsedMilliseconds))
                .ToList();
        }
        catch (Exception ex)
        {
            return CreateFailure(filePath, $"Не удалось разобрать ответ модели: {ex.Message}", stopwatch);
        }
    }

    private static VehicleResult CreateVehicleResult(
        VehicleResult source,
        string filePath,
        long processingTimeMs)
    {
        string plateReadStatus = Normalize(source.PlateReadStatus, "Не читается");
        double plateConfidence = Math.Clamp(source.PlateConfidence, 0, 1);
        bool reliablePlate = IsReliablePlateReadStatus(plateReadStatus) &&
                            plateConfidence >= 0.90 &&
                            IsActualPlate(source.PlateNumber);

        string loadVisibility = Normalize(source.LoadVisibility, "Не видно содержимое кузова");
        string loadStatus = NormalizeLoadStatus(source.LoadStatus);
        double loadConfidence = Math.Clamp(source.LoadConfidence, 0, 1);
        bool reliableLoad = loadVisibility.Equals("Содержимое кузова видно", StringComparison.OrdinalIgnoreCase) &&
                            loadConfidence >= 0.70 &&
                            loadStatus != "Не определено" &&
                            source.LoadPercentage is >= 0 and <= 100;

        return new VehicleResult
        {
            FileName = Path.GetFileName(filePath),
            FilePath = filePath,
            HasTechnique = source.HasTechnique,
            VehicleType = Normalize(source.VehicleType, source.HasTechnique ? "Не определено" : "Нет"),
            Make = Normalize(source.Make, "Не определено"),
            Model = Normalize(source.Model, "Не определено"),
            PlateNumber = reliablePlate ? Normalize(source.PlateNumber, "Не читается") : "Не читается",
            PlateCountry = reliablePlate ? Normalize(source.PlateCountry, "Не определена") : "Не определена",
            PlateReadStatus = reliablePlate ? "Надёжно" : plateReadStatus,
            PlateConfidence = plateConfidence,
            CapturedAt = source.CapturedAt,
            CameraId = Normalize(source.CameraId, "Не определена"),
            CheckpointStatus = NormalizeCheckpointStatus(source.CheckpointStatus),
            TransportUnit = Normalize(source.TransportUnit, "Не определено"),
            CabColor = Normalize(source.CabColor, "Не определено"),
            BodyColor = Normalize(source.BodyColor, "Не определено"),
            AxleConfiguration = Normalize(source.AxleConfiguration, "Не определено"),
            LoadStatus = reliableLoad ? loadStatus : "Не определено",
            LoadPercentage = reliableLoad ? source.LoadPercentage : null,
            LoadVisibility = loadVisibility,
            LoadConfidence = loadConfidence,
            Confidence = Math.Clamp(source.Confidence, 0, 1),
            ProcessingTimeMs = processingTimeMs,
            Status = "Успешно"
        };
    }

    private static List<VehicleResult> CreateFailure(
        string filePath,
        string status,
        System.Diagnostics.Stopwatch stopwatch)
    {
        return
        [
            new VehicleResult
            {
                FileName = Path.GetFileName(filePath),
                FilePath = filePath,
                ProcessingTimeMs = stopwatch.ElapsedMilliseconds,
                Status = status
            }
        ];
    }

    private static object CreateRequestBody(string model, IReadOnlyList<VisionImage> visionImages)
    {
        var content = new List<object>
        {
            new { type = "text", text = CreatePrompt(visionImages.Count > 1) }
        };

        foreach (var image in visionImages)
        {
            content.Add(new { type = "image_url", image_url = new { url = image.DataUrl } });
        }

        return new
        {
            model,
            messages = new object[]
            {
                new { role = "user", content = content.ToArray() }
            },
            temperature = 0,
            max_completion_tokens = 500,
            reasoning_effort = "none"
        };
    }

    private static string CreatePrompt(bool containsPlateDetails)
    {
        string detailsInstruction = containsPlateDetails
            ? "The input includes the full frame followed by one enlarged checkpoint zone. The full frame selects the primary transport; the enlarged zone is for reading its plate only. Never take a plate from a different vehicle shown in the enlarged image."
            : "The input contains only the full frame.";

        return """
        You are processing one security-camera frame from a weighbridge checkpoint. Return exactly one JSON object and nothing else. DO NOT wrap in markdown blocks.
        Return exactly one record for the PRIMARY transport unit on the weighbridge or in the approach corridor. 
        All descriptive values must be in Russian.
        __DETAILS_INSTRUCTION__

        {
          "vehicles": [
            {
              "has_technique": true,
              "vehicle_type": "ТИП_ТЕХНИКИ",
              "transport_unit": "Самосвал с прицепом / Самосвал без прицепа",
              "make": "МАРКА_АВТОМОБИЛЯ",
              "model": "МОДЕЛЬ",
              "cab_color": "ЦВЕТ_КАБИНЫ",
              "body_color": "ЦВЕТ_КУЗОВА",
              "axle_configuration": "КОЛЕСНАЯ_ФОРМУЛА",
              "plate_number": "НОМЕР_ИЛИ_Не_читается",
              "plate_bbox": [0.0, 0.0, 0.0, 0.0],
              "plate_country": "СТРАНА",
              "plate_read_status": "Надёжно / Не читается",
              "plate_confidence": 0.0,
              "captured_at": "YYYY-MM-DDTHH:mm:ss",
              "camera_id": "ИДЕНТИФИКАТОР_КАМЕРЫ",
              "checkpoint_status": "На весах / Перед весами / После весов",
              "load_visibility": "Содержимое кузова видно / Не видно содержимое кузова",
              "load_status": "Пустой / Частично загружен / Загружен",
              "load_percentage": 0,
              "load_confidence": 0.0,
              "confidence": 0.0
            }
          ]
        }

        Rules:
        - CRITICAL: Output ONLY valid JSON. No markdown formatting.
        - DATE EXTRACT: The camera uses US format (MM-DD-YYYY). E.g., "01-30-2019" is January 30, 2019. Convert to ISO format.
        - WEIGHBRIDGE STATUS: On metal platform = "На весах". Past the platform on concrete = "После весов".
        - TRAILER CHECK: If there is a trailer attached, use "с прицепом". If not, use "без прицепа".
        - PLATE READING (LATIN STRICT): Kazakhstan plates use LATIN letters (e.g., L, B, A, V). Do NOT translate or transliterate into Cyrillic (e.g., do not write АЛВ instead of ALV). Output the exact English/Latin characters printed on the plate (e.g., "592 LBA 10"). If completely unreadable, return "Не читается".
        - PLATE BBOX: If you read the plate, provide its bounding box as [x, y, width, height] in relative coordinates (0.0 to 1.0).
        """.Replace("__DETAILS_INSTRUCTION__", detailsInstruction, StringComparison.Ordinal);
    }

    private static List<VehicleResult> DeserializeVehicles(string json)
    {
        using var document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("vehicles", out JsonElement vehiclesElement))
        {
            if (vehiclesElement.ValueKind == JsonValueKind.Array)
            {
                return JsonSerializer.Deserialize<List<VehicleResult>>(vehiclesElement.GetRawText(), JsonOptions) ?? [];
            }

            if (vehiclesElement.ValueKind == JsonValueKind.Object)
            {
                var singleInWrapper = JsonSerializer.Deserialize<VehicleResult>(vehiclesElement.GetRawText(), JsonOptions);
                return singleInWrapper is null ? [] : [singleInWrapper];
            }
        }

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("has_technique", out _))
        {
            var directResult = JsonSerializer.Deserialize<VehicleResult>(json, JsonOptions);
            return directResult is null ? [] : [directResult];
        }

        if (root.ValueKind == JsonValueKind.Array)
        {
            return JsonSerializer.Deserialize<List<VehicleResult>>(json, JsonOptions) ?? [];
        }

        return [];
    }

    private static IReadOnlyList<VisionImage> CreateVisionImages(
        string filePath,
        byte[] sourceBytes,
        bool enhancedPlateOcr)
    {
        var images = new List<VisionImage>();

        try
        {
            using var source = Image.FromFile(filePath);
            using (var fullFrame = ResizeImage(source, FullFrameMaxWidth))
            {
                images.Add(new VisionImage(
                    "Полный кадр. По нему выбери основной транспорт на КПП.",
                    ToJpegDataUrl(fullFrame)));
            }

            if (enhancedPlateOcr)
            {
                var (rectangle, label) = GetDetailRegions(source.Width, source.Height).Single();
                using var detail = CreateDetailImage(source, rectangle);
                images.Add(new VisionImage(
                    $"Увеличенный фрагмент: {label}. Используй его только для уточнения номера основного транспорта.",
                    ToJpegDataUrl(detail)));
            }
        }
        catch
        {
            string mimeType = GetMimeType(filePath);
            images.Add(new VisionImage(
                "Полный кадр. По нему выбери основной транспорт на КПП.",
                $"data:{mimeType};base64,{Convert.ToBase64String(sourceBytes)}"));
        }

        return images;
    }

    private static IEnumerable<(Rectangle Rectangle, string Label)> GetDetailRegions(int width, int height)
    {
        int cropWidth = width / 2;
        int cropHeight = height / 2;
        int startX = width / 4;  
        int startY = height / 4; 

        yield return (
            new Rectangle(startX, startY, cropWidth, cropHeight),
            "Центральная зона (дальний план)"
        );
    }

    private static Bitmap CreateDetailImage(Image source, Rectangle crop)
    {
        int targetWidth = Math.Min(DetailImageMaxWidth, crop.Width * 2);
        int targetHeight = Math.Max(1, (int)Math.Round(crop.Height * (targetWidth / (double)crop.Width)));
        var detail = new Bitmap(targetWidth, targetHeight, PixelFormat.Format24bppRgb);

        using var graphics = Graphics.FromImage(detail);
        graphics.Clear(Color.White);
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.DrawImage(source, new Rectangle(0, 0, targetWidth, targetHeight), crop, GraphicsUnit.Pixel);
        return detail;
    }

    private static Bitmap ResizeImage(Image source, int maxWidth)
    {
        int targetWidth = Math.Min(maxWidth, source.Width);
        int targetHeight = Math.Max(1, (int)Math.Round(source.Height * (targetWidth / (double)source.Width)));
        var resized = new Bitmap(targetWidth, targetHeight, PixelFormat.Format24bppRgb);

        using var graphics = Graphics.FromImage(resized);
        graphics.Clear(Color.White);
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.DrawImage(source, new Rectangle(0, 0, targetWidth, targetHeight));
        return resized;
    }

    private static string ToJpegDataUrl(Image image)
    {
        using var stream = new MemoryStream();
        SaveDetailAsJpeg(image, stream);
        return $"data:image/jpeg;base64,{Convert.ToBase64String(stream.ToArray())}";
    }

    private static void SaveDetailAsJpeg(Image detail, Stream destination)
    {
        var encoder = ImageCodecInfo.GetImageEncoders()
            .First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);
        detail.Save(destination, encoder, parameters);
    }

    private static string GetMimeType(string filePath) => Path.GetExtension(filePath).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".webp" => "image/webp",
        _ => "image/jpeg"
    };

    private static string ExtractJson(string content)
    {
        int start = content.IndexOf('{');
        int end = content.LastIndexOf('}');
        return start >= 0 && end > start
            ? content[start..(end + 1)]
            : content;
    }

    private static string Normalize(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static bool IsActualPlate(string? plateNumber) =>
        !string.IsNullOrWhiteSpace(plateNumber) &&
        !plateNumber.Contains("не чит", StringComparison.OrdinalIgnoreCase) &&
        !plateNumber.Contains("отсутств", StringComparison.OrdinalIgnoreCase) &&
        !plateNumber.Contains("не определ", StringComparison.OrdinalIgnoreCase);

    private static bool IsRequestTooLarge(string responseString) =>
        responseString.Contains("Request too large", StringComparison.OrdinalIgnoreCase);

    private static bool IsReliablePlateReadStatus(string value) =>
        value.Equals("Надёжно", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Надежно", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeCheckpointStatus(string? value) => Normalize(value, "Не определено") switch
    {
        "На весах" => "На весах",
        "Перед весами" => "Перед весами",
        "После весов" => "После весов",
        _ => "Не определено"
    };

    private static string NormalizeLoadStatus(string? value) => Normalize(value, "Не определено") switch
    {
        "Пустой" => "Пустой",
        "Частично загружен" => "Частично загружен",
        "Загружен" => "Загружен",
        _ => "Не определено"
    };

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, string responseString)
    {
        TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is { } headerDelay && headerDelay > TimeSpan.Zero)
        {
            return headerDelay + TimeSpan.FromMilliseconds(500);
        }

        Match match = RetryAfterInBody.Match(responseString);
        if (match.Success && double.TryParse(
                match.Groups["seconds"].Value,
                CultureInfo.InvariantCulture,
                out double seconds))
        {
            return TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(500);
        }

        return TimeSpan.FromSeconds(30);
    }
}

internal sealed record VisionImage(string Label, string DataUrl);

public sealed class VehicleResultWrapper
{
    [JsonPropertyName("vehicles")]
    public List<VehicleResult> Vehicles { get; set; } = [];
}
