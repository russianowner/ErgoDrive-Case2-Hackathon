using AutoFleet_Vision.Components.Models;
using System.Text;
using System.Text.Json;

namespace AutoFleet_Vision.Components.Services;

public class TelegramBotService
{
    private readonly HttpClient _httpClient;
    private readonly string _botToken;

    public TelegramBotService(HttpClient httpClient, IConfiguration config)
    {
        _httpClient = httpClient;
        _botToken = config["Telegram:BotToken"] ?? string.Empty;
    }

    public async Task SendVehicleAlertAsync(VehicleResult vehicle, string chatId)
    {
        if (string.IsNullOrWhiteSpace(_botToken) || string.IsNullOrWhiteSpace(chatId) || !vehicle.HasTechnique)
            return;

        string message = $@"🚨 *Новая фиксация на КПП*
            📷 *Камера:* {vehicle.CameraId}
            🚛 *Транспорт:* {vehicle.Make} {vehicle.Model}
            🏷 *Гос. номер:* {(vehicle.PlateNumber == "Не читается" ? "⚠️ Не распознан" : vehicle.PlateNumber)}
            📦 *Загрузка:* {vehicle.LoadStatus} ({vehicle.LoadPercentage}%)
            ⏱ *Время:* {vehicle.CapturedAt?.ToString("dd.MM.yyyy HH:mm:ss") ?? "Неизвестно"}";

        var payload = new
        {
            chat_id = chatId,
            text = message,
            parse_mode = "Markdown"
        };

        try
        {
            var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync($"https://api.telegram.org/bot{_botToken}/sendMessage", content);

            // Если телеграм ругается (например, бот не админ), выводим ошибку в консоль
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[TG ERROR] {await response.Content.ReadAsStringAsync()}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TG EXCEPTION] {ex.Message}");
        }
    }
    public async Task SendDocumentAsync(string chatId, string fileName, string fileContent)
    {
        if (string.IsNullOrWhiteSpace(_botToken) || string.IsNullOrWhiteSpace(chatId))
            return;

        try
        {
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent(chatId), "chat_id");

            var fileBytes = Encoding.UTF8.GetBytes(fileContent);
            var fileData = new ByteArrayContent(fileBytes);
            form.Add(fileData, "document", fileName);

            var response = await _httpClient.PostAsync($"https://api.telegram.org/bot{_botToken}/sendDocument", form);

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[TG FILE ERROR] {await response.Content.ReadAsStringAsync()}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TG EXCEPTION] {ex.Message}");
        }
    }
}