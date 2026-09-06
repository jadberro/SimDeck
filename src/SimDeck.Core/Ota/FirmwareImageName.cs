using System.Text.RegularExpressions;

namespace SimDeck.Core.Ota;

/// <summary>
/// Works out module type and version from a firmware file name.
///
/// Exists because the Arduino IDE does not produce the tidy
/// module_1.2.3.bin the manifest would prefer. "Export compiled binary"
/// writes a folder containing:
///
///   accu_panel.ino.bin              the app image, the one we want
///   accu_panel.ino.merged.bin       app + bootloader from flash offset 0
///   accu_panel.ino.bootloader.bin
///   accu_panel.ino.partitions.bin
///
/// Only the first is a valid over-the-air image. A merged image written to
/// an app partition will not boot, and the failure looks like a hardware
/// fault rather than the wrong file, so it is worth refusing outright.
/// </summary>
public static class FirmwareImageName
{
    private static readonly string[] NotOtaImages =
    {
        ".merged.bin", ".bootloader.bin", ".partitions.bin", ".boot_app0.bin",
    };

    private static readonly Regex TrailingVersion =
        new(@"^(?<name>.+?)[_-](?<ver>\d+(?:\.\d+)*)$", RegexOptions.Compiled);

    public static bool TryParse(string fileName, out string type,
                                out string version, out string reject)
        => TryParse(fileName, DateTime.Now, out type, out version, out reject);

    /// <summary>Overload taking the clock so the date-stamp path is testable.</summary>
    public static bool TryParse(string fileName, DateTime now, out string type,
                                out string version, out string reject)
    {
        type = ""; version = ""; reject = "";
        var lower = fileName.ToLowerInvariant();

        foreach (var bad in NotOtaImages)
        {
            if (lower.EndsWith(bad, StringComparison.Ordinal))
            {
                reject = $"{fileName} is not an over-the-air image.\n\n"
                       + "Use the plain .ino.bin from the same folder. Merged "
                       + "and bootloader images start at flash offset 0 and "
                       + "will not boot from an app partition.";
                return false;
            }
        }

        if (!lower.EndsWith(".bin", StringComparison.Ordinal))
        {
            reject = "Select a .bin file.";
            return false;
        }

        var stem = fileName[..^4];

        // Strip what the toolchain appends: accu_panel.ino.esp32s3
        var dot = stem.IndexOf(".ino", StringComparison.OrdinalIgnoreCase);
        if (dot > 0) stem = stem[..dot];

        var m = TrailingVersion.Match(stem);
        if (m.Success)
        {
            type = m.Groups["name"].Value;
            version = m.Groups["ver"].Value;
        }
        else
        {
            // Normal Arduino case: no version in the name. Date stamp it.
            // Version comparison is numeric, so 2026.09.05 orders correctly
            // against both other dates and semver.
            type = stem;
            version = now.ToString("yyyy.MM.dd");
        }

        if (type.Length == 0)
        {
            reject = $"Could not work out a module type from {fileName}.";
            return false;
        }
        return true;
    }
}
