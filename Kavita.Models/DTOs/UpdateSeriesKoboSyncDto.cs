using System.Collections.Generic;

namespace Kavita.Models.DTOs;

public sealed record UpdateSeriesKoboSyncDto
{
    public bool AllowKoboSync { get; init; }
    public IList<int> SeriesIds { get; init; } = [];
}
