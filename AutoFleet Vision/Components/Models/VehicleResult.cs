using System.Text.Json.Serialization;
using System.ComponentModel.DataAnnotations;

namespace AutoFleet_Vision.Components.Models;

public class VehicleResult
{
    [Key]
    public int Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("has_technique")]
    public bool HasTechnique { get; set; }

    [JsonPropertyName("vehicle_type")]
    public string VehicleType { get; set; } = "Не определено";

    [JsonPropertyName("make")]
    public string Make { get; set; } = "Не определено";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "Не определено";

    [JsonPropertyName("plate_number")]
    public string PlateNumber { get; set; } = "Отсутствует / не виден";

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; } = 1.0;

    public string Status { get; set; } = "Ожидание";
    public long ProcessingTimeMs { get; set; }

    [JsonPropertyName("plate_country")]
    public string PlateCountry { get; set; } = "Не определена";

    [JsonPropertyName("plate_read_status")]
    public string PlateReadStatus { get; set; } = "Не читается";

    [JsonPropertyName("plate_confidence")]
    public double PlateConfidence { get; set; }

    [JsonPropertyName("captured_at")]
    [JsonConverter(typeof(CameraTimestampJsonConverter))]
    public DateTime? CapturedAt { get; set; }

    [JsonPropertyName("camera_id")]
    public string CameraId { get; set; } = "Не определена";

    [JsonPropertyName("checkpoint_status")]
    public string CheckpointStatus { get; set; } = "Не определено";

    [JsonPropertyName("transport_unit")]
    public string TransportUnit { get; set; } = "Не определено";

    [JsonPropertyName("cab_color")]
    public string CabColor { get; set; } = "Не определено";

    [JsonPropertyName("body_color")]
    public string BodyColor { get; set; } = "Не определено";

    [JsonPropertyName("axle_configuration")]
    public string AxleConfiguration { get; set; } = "Не определено";

    [JsonPropertyName("load_status")]
    public string LoadStatus { get; set; } = "Не определено";

    [JsonPropertyName("load_percentage")]
    public int? LoadPercentage { get; set; }

    [JsonPropertyName("load_visibility")]
    public string LoadVisibility { get; set; } = "Не видно содержимое кузова";

    [JsonPropertyName("load_confidence")]
    public double LoadConfidence { get; set; }

    [JsonPropertyName("bbox")]
    public double[]? Bbox { get; set; }
    [JsonPropertyName("plate_bbox")]
    public float[]? PlateBbox { get; set; }
}

public sealed class CameraTimestampJsonConverter : System.Text.Json.Serialization.JsonConverter<DateTime?>
{
    private static readonly string[] Formats =
    [
        "yyyy-MM-dd'T'HH:mm:ss",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF",
        "MM-dd-yyyy ddd HH:mm:ss",
        "MM-dd-yyyy HH:mm:ss",
        "MM/dd/yyyy HH:mm:ss"
    ];

    public override DateTime? Read(
        ref System.Text.Json.Utf8JsonReader reader,
        Type typeToConvert,
        System.Text.Json.JsonSerializerOptions options)
    {
        if (reader.TokenType == System.Text.Json.JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != System.Text.Json.JsonTokenType.String)
        {
            throw new System.Text.Json.JsonException("Время камеры должно быть строкой или null.");
        }

        string? value = reader.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTime.TryParseExact(value, Formats, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AllowWhiteSpaces, out DateTime capturedAt))
        {
            return DateTime.SpecifyKind(capturedAt, DateTimeKind.Unspecified);
        }

        throw new System.Text.Json.JsonException($"Неизвестный формат времени камеры: {value}");
    }

    public override void Write(
        System.Text.Json.Utf8JsonWriter writer,
        DateTime? value,
        System.Text.Json.JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(value.Value.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture));
    }
}
