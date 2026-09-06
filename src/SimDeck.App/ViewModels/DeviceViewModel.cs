using SimDeck.Core;
using SimDeck.Core.Ota;

namespace SimDeck.App.ViewModels;

public sealed class ValueRow : ObservableObject
{
    private string _display = "—";
    public required string Name { get; init; }
    public string Display { get => _display; set => Set(ref _display, value); }
}

public sealed class DeviceViewModel : ObservableObject
{
    private string _subtitle = "";
    private string _statusColour = "#7B8894";
    private string _otaLine = "";
    private int _otaPercent;
    private bool _otaVisible;
    private string? _updateAvailable;

    public required string Id { get; init; }
    public string Name { get; set; } = "";
    public string ModuleType { get; set; } = "";

    public List<ValueRow> Values { get; } = new();

    public string Subtitle { get => _subtitle; set => Set(ref _subtitle, value); }
    public string StatusColour { get => _statusColour; set => Set(ref _statusColour, value); }
    public string OtaLine { get => _otaLine; set => Set(ref _otaLine, value); }
    public int OtaPercent { get => _otaPercent; set => Set(ref _otaPercent, value); }
    public bool OtaVisible { get => _otaVisible; set => Set(ref _otaVisible, value); }
    public string? UpdateAvailable { get => _updateAvailable; set => Set(ref _updateAvailable, value); }

    public void Update(ModuleInfo m, OtaStatus ota, FirmwareEntry? entry)
    {
        Name = m.Name;
        ModuleType = m.ModuleType;
        Subtitle = $"{m.Id} · {m.Ip} · {m.Rate:0}Hz · fw {m.Firmware}";

        UpdateAvailable = entry is not null && OtaTracker.IsNewer(entry.Version, m.Firmware)
            ? entry.Version : null;

        StatusColour = !m.Online ? "#D9534F"
                     : UpdateAvailable is not null ? "#E0A531"
                     : "#2FBF5F";

        // Rebuild rows only when the shape changes; otherwise update in place
        // so the list does not flicker at 30Hz.
        if (Values.Count != m.Subscriptions.Count)
        {
            Values.Clear();
            foreach (var s in m.Subscriptions) Values.Add(new ValueRow { Name = s });
            Raise(nameof(Values));
        }

        for (int i = 0; i < Values.Count && i < m.Values.Length; i++)
        {
            var v = m.Values[i];
            Values[i].Display = float.IsNaN(v) ? "—" : v.ToString("0");
        }

        OtaVisible = ota.State is OtaState.Offered or OtaState.Running or OtaState.Failed;
        OtaPercent = ota.Percent;
        OtaLine = ota.State switch
        {
            OtaState.Offered => "Update offered",
            OtaState.Running => $"Updating · {ota.Percent}%",
            OtaState.Failed  => $"Update failed · {ota.Message}",
            _ => "",
        };
    }
}

public sealed class FirmwareViewModel : ObservableObject
{
    private string _versionLine = "";
    private string _deviceLine = "";
    private bool _behind;

    public required string ModuleType { get; init; }
    public string VersionLine { get => _versionLine; set => Set(ref _versionLine, value); }
    public string DeviceLine { get => _deviceLine; set => Set(ref _deviceLine, value); }
    public bool Behind { get => _behind; set => Set(ref _behind, value); }
}
