using Microsoft.JSInterop;

namespace LabHop.Components.Shared;

/// <summary>The browser's time zone, read once per circuit. Falls back to UTC if it cannot be determined.</summary>
public sealed class BrowserTimeZone(IJSRuntime js)
{
    private TimeZoneInfo? zone;

    public async ValueTask<TimeZoneInfo> GetAsync()
    {
        if (zone is not null)
        {
            return zone;
        }

        try
        {
            var id = await js.InvokeAsync<string>("labhop.timeZone");
            zone = TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception exception) when (exception is JSException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zone = TimeZoneInfo.Utc;
        }

        return zone;
    }
}
