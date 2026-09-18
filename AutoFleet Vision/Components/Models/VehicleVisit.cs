namespace AutoFleet_Vision.Components.Models;

public sealed class VehicleVisit
{
    public string VisitId { get; init; } = string.Empty;
    public string PlateNumber { get; init; } = string.Empty;
    public string VehicleName { get; init; } = "Не определено";
    public DateTime StartedAt { get; init; }
    public DateTime? EndedAt { get; init; }
    public TimeSpan? Duration { get; init; }
    public int EventCount { get; init; }
    public string LoadChange { get; init; } = "Нет данных";
    public string State { get; init; } = "Ожидает второй кадр";
    public string MatchBasis { get; init; } = "Надёжный номер и профиль";
    public string RiskStatus { get; init; } = "Нет оснований для сигнала";
}
