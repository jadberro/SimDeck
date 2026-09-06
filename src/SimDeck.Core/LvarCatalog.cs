using System.Text.RegularExpressions;

namespace SimDeck.Core;

/// <summary>
/// Finds variable names without asking the simulator.
///
/// The Lua scan depends on ipc.getLvarList, which is not present in every
/// FSUIPC build - and when it is missing you get an empty list with no
/// explanation. The aircraft's own package files are always there, always
/// readable, and do not care which FSUIPC you have.
/// </summary>
public static class LvarCatalog
{
    // <VAR_NAME>L:FNX320_BRAKE</VAR_NAME>, VAR_NAME="L:FNX320_BRAKE",
    // and bare L: references inside RPN code all appear in these files.
    private static readonly Regex VarNameElement =
        new(@"<VAR_NAME>\s*([^<]+?)\s*</VAR_NAME>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VarNameAttribute =
        new(@"VAR_NAME\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LocalRef =
        new(@"\bL:([A-Za-z0-9_\.]+)", RegexOptions.Compiled);

    /// <summary>Pull every local variable name out of one behaviour file.</summary>
    public static IReadOnlyList<string> FromXml(string xml)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        void Add(string raw)
        {
            var n = raw.Trim();
            // Strip the L: prefix - FSUIPC's readLvar wants the bare name.
            if (n.StartsWith("L:", StringComparison.OrdinalIgnoreCase)) n = n[2..];
            n = n.Trim();
            if (n.Length == 0 || n.Length > 120) return;
            if (n.Contains(' ') || n.Contains('"')) return;
            names.Add(n);
        }

        foreach (Match m in VarNameElement.Matches(xml)) Add(m.Groups[1].Value);
        foreach (Match m in VarNameAttribute.Matches(xml)) Add(m.Groups[1].Value);
        foreach (Match m in LocalRef.Matches(xml)) Add(m.Groups[1].Value);

        return names.OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    public static IReadOnlyList<string> FromFile(string path)
        => FromXml(File.ReadAllText(path));

    /// <summary>
    /// Locate the simulator's package folder.
    ///
    /// The Community folder is not in a fixed place: Store and Steam differ,
    /// MSFS 2020 and 2024 differ, and users relocate it. UserCfg.opt records
    /// wherever it actually is, so read that rather than guessing.
    /// </summary>
    public static IReadOnlyList<string> FindPackageRoots()
    {
        var roots = new List<string>();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var configs = new[]
        {
            // MSFS 2024
            Path.Combine(local, @"Packages\Microsoft.Limitless_8wekyb3d8bbwe\LocalCache\UserCfg.opt"),
            Path.Combine(roaming, @"Microsoft Flight Simulator 2024\UserCfg.opt"),
            // MSFS 2020
            Path.Combine(local, @"Packages\Microsoft.FlightSimulator_8wekyb3d8bbwe\LocalCache\UserCfg.opt"),
            Path.Combine(roaming, @"Microsoft Flight Simulator\UserCfg.opt"),
        };

        foreach (var cfg in configs)
        {
            if (!File.Exists(cfg)) continue;
            try
            {
                foreach (var line in File.ReadAllLines(cfg))
                {
                    var m = Regex.Match(line, @"InstalledPackagesPath\s+""([^""]+)""",
                                        RegexOptions.IgnoreCase);
                    if (m.Success && Directory.Exists(m.Groups[1].Value))
                        roots.Add(m.Groups[1].Value);
                }
            }
            catch (IOException) { }
        }
        return roots;
    }

    /// <summary>
    /// Behaviour files for an add-on, e.g. "fnx". Searching is bounded to
    /// matching package folders, because a full recursive walk of a MSFS
    /// package tree takes minutes and hits files it cannot read.
    /// </summary>
    public static IReadOnlyList<string> FindBehaviourFiles(string packageMatch = "fnx")
    {
        var found = new List<string>();

        foreach (var root in FindPackageRoots())
        {
            foreach (var sub in new[] { "Community", "Official\\OneStore", "Official\\Steam", "" })
            {
                var dir = string.IsNullOrEmpty(sub) ? root : Path.Combine(root, sub);
                if (!Directory.Exists(dir)) continue;

                IEnumerable<string> packages;
                try { packages = Directory.EnumerateDirectories(dir); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }

                foreach (var pkg in packages)
                {
                    var name = Path.GetFileName(pkg);
                    if (name.IndexOf(packageMatch, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    try
                    {
                        found.AddRange(Directory.EnumerateFiles(
                            pkg, "*ehavior*.xml", SearchOption.AllDirectories));
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Rank names by how likely they are to be what you are hunting for.
    /// Every term that matches scores, so BRAKE_ACCU_PRESS beats BRAKE_TEMP.
    /// </summary>
    public static IReadOnlyList<string> Rank(IEnumerable<string> names,
                                             params string[] terms)
    {
        return names
            .Select(n => (name: n,
                          score: terms.Count(t =>
                              n.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.name, StringComparer.Ordinal)
            .Select(x => x.name)
            .ToList();
    }
}
