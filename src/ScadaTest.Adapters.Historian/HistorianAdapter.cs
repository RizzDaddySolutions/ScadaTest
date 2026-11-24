using Microsoft.Data.SqlClient;
using ScadaTest.Core;

namespace ScadaTest.Adapters.Historian;

/// <summary>
/// Reads AVEVA Historian through its SQL interface (the <c>Runtime</c> database, <c>History</c> view).
/// </summary>
/// <remarks>
/// Settings:
/// <list type="bullet">
/// <item><c>connectionString</c> (required), e.g. <c>Server=hist01;Database=Runtime;Integrated Security=true;TrustServerCertificate=true</c></item>
/// <item><c>alarmEventsQuery</c> (optional): SQL returning <c>EventTime</c> (UTC) and <c>EventKind</c>
/// (raise / ack / rtn) for parameters <c>@source</c>, <c>@condition</c>, <c>@from</c>, <c>@to</c>.
/// The alarm schema depends on how alarms are stored (Historian events or the Alarm DB Logger), so it is set per site.</item>
/// </list>
/// Verify the History query and time zone handling against your Historian version during the spike.
/// </remarks>
[ScadaAdapter("aveva-historian")]
public sealed class HistorianAdapter : IScadaAdapter, IHistoryClient
{
    internal const string HistoryQuery = """
        SELECT DateTime, Value, vValue, QualityDetail
        FROM Runtime.dbo.History
        WHERE TagName = @tag
          AND DateTime >= @from AND DateTime <= @to
          AND wwRetrievalMode = @mode
          AND wwTimeZone = 'UTC'
          {0}
        ORDER BY DateTime
        """;

    private AdapterConfig _config = null!;
    private string _connectionString = "";

    public string Id => "aveva-historian";

    public Capability Capabilities => Capability.History;

    public async Task ConnectAsync(AdapterConfig config, CancellationToken ct)
    {
        _config = config;
        _connectionString = config.Require("connectionString");
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false); // fail fast on bad credentials
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public async Task<IReadOnlyList<HistorySample>> QueryAsync(
        TagRef tag, DateTimeOffset from, DateTimeOffset to, RetrievalMode mode, TimeSpan? interval, CancellationToken ct)
    {
        var sql = string.Format(System.Globalization.CultureInfo.InvariantCulture, HistoryQuery,
            mode == RetrievalMode.Cyclic && interval is not null ? "AND wwResolution = @resolution" : string.Empty);

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@tag", _config.TagMap.Resolve(tag, _config.Name));
        command.Parameters.AddWithValue("@from", from.UtcDateTime);
        command.Parameters.AddWithValue("@to", to.UtcDateTime);
        command.Parameters.AddWithValue("@mode", mode == RetrievalMode.Cyclic ? "Cyclic" : "Delta");
        if (mode == RetrievalMode.Cyclic && interval is { } i)
            command.Parameters.AddWithValue("@resolution", (int)i.TotalMilliseconds);

        var samples = new List<HistorySample>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var time = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(0), DateTimeKind.Utc));
            object? value = reader.IsDBNull(1) ? (reader.IsDBNull(2) ? null : reader.GetValue(2)) : reader.GetValue(1);
            var qualityDetail = reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetValue(3), System.Globalization.CultureInfo.InvariantCulture);
            samples.Add(new HistorySample(time, value, MapQuality(qualityDetail)));
        }
        return samples;
    }

    public async Task<IReadOnlyList<AlarmEvent>> QueryAlarmEventsAsync(AlarmRef alarm, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var sql = _config.Get("alarmEventsQuery")
            ?? throw new NotSupportedException("Set 'alarmEventsQuery' on the Historian adapter to verify alarm history.");

        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@source", alarm.Source);
        command.Parameters.AddWithValue("@condition", alarm.Condition);
        command.Parameters.AddWithValue("@from", from.UtcDateTime);
        command.Parameters.AddWithValue("@to", to.UtcDateTime);

        var events = new List<AlarmEvent>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var time = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(reader.GetOrdinal("EventTime")), DateTimeKind.Utc));
            var kind = reader.GetString(reader.GetOrdinal("EventKind")).ToLowerInvariant() switch
            {
                "raise" or "alarm" or "unack_alm" => AlarmEventKind.Raise,
                "ack" or "ack_alm" => AlarmEventKind.Ack,
                _ => AlarmEventKind.ReturnToNormal,
            };
            events.Add(new AlarmEvent(alarm, kind, time));
        }
        return events;
    }

    /// <summary>Historian QualityDetail 192 is OPC "good"; 64-127 uncertain; anything else bad.</summary>
    internal static Quality MapQuality(int qualityDetail) => qualityDetail switch
    {
        >= 192 and <= 255 => Quality.Good,
        >= 64 and < 128 => Quality.Uncertain,
        _ => Quality.Bad,
    };
}
