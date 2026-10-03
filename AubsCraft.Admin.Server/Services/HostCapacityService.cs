using AubsCraft.Admin.Server.Models;

namespace AubsCraft.Admin.Server.Services;

/// <summary>
/// Warns before starting a server the machine probably cannot carry. There is deliberately NO hard limit on
/// how many servers exist or run (the hardware will change); instead the machine's real RAM and cores are
/// compared with what the running servers (plus the one being started) are configured to use.
///
/// Estimates: a Java server uses its -Xmx heap plus about a quarter more (at least 512 MB) for metaspace,
/// thread stacks, direct buffers and the JIT. The OS, this panel and the proxy keep OsReserveMb. A server's
/// main (tick) thread is effectively single-threaded, so more running servers than cores means they compete
/// for CPU and lag.
/// </summary>
public class HostCapacityService
{
    public const int OsReserveMb = 1536;

    private readonly ServerManager _servers;

    public HostCapacityService(ServerManager servers)
    {
        _servers = servers;
    }

    /// <summary>Estimated resident memory of a server given its heap size.</summary>
    public static int EstimatedFootprintMb(int heapMb) => heapMb + Math.Max(512, heapMb / 4);

    /// <summary>The machine's total memory in MB: /proc/meminfo MemTotal on Linux, the GC's view elsewhere.</summary>
    public static long TotalMemoryMb()
    {
        var kb = ReadMemInfoKb("MemTotal");
        if (kb != null) return kb.Value / 1024;
        return GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
    }

    private static long? ReadMemInfoKb(string key)
    {
        try
        {
            if (!File.Exists("/proc/meminfo")) return null;
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (!line.StartsWith(key + ":", StringComparison.Ordinal)) continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                return long.Parse(parts[1]);
            }
        }
        catch (IOException) { }
        return null;
    }

    /// <summary>
    /// The capacity picture if <paramref name="toStart"/> were started now (null = as things are). A server
    /// counts as running when its RCON answered on the last monitor poll.
    /// </summary>
    public HostCapacityDto Evaluate(ServerInstance? toStart = null) =>
        Evaluate(TotalMemoryMb(), Environment.ProcessorCount,
            _servers.All.Where(s => s.LastStatus?.Connected == true).Select(s => s.Definition).ToList(),
            toStart?.Definition);

    /// <summary>The pure calculation (testable with any machine size).</summary>
    public static HostCapacityDto Evaluate(long totalMb, int cores, IReadOnlyList<ServerDefinition> running, ServerDefinition? toStart)
    {
        var after = running.Where(r => toStart == null || r.Id != toStart.Id).ToList();
        if (toStart != null) after.Add(toStart);

        var neededMb = after.Sum(s => EstimatedFootprintMb(s.MemoryMb)) + OsReserveMb;
        var warnings = new List<string>();
        if (neededMb > totalMb)
        {
            warnings.Add(
                $"{after.Count} running server(s) would need about {neededMb:N0} MB (their heaps plus Java overhead, plus " +
                $"{OsReserveMb:N0} MB for the system), but the machine has {totalMb:N0} MB. Expect swapping, lag or a crash. " +
                "Stop another server first, lower a server's memory, or give the machine more RAM.");
        }
        if (after.Count > cores)
        {
            warnings.Add(
                $"{after.Count} running servers on {cores} CPU core(s): each server's main thread needs a core of its own, " +
                "so they will slow each other down.");
        }
        return new HostCapacityDto(totalMb, cores, after.Count, neededMb, warnings);
    }
}

/// <summary>What starting (or running) the servers would ask of the machine, with any warnings.</summary>
public record HostCapacityDto(long TotalMemoryMb, int Cores, int RunningServers, long NeededMemoryMb, List<string> Warnings);
