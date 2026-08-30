namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Instrumentation;

internal static class RoslynStateTraceParser
{
    private const int MaxScalarLength = 256;
    private const int MaxTraceLineLength = 4096;

    public static IReadOnlyList<RoslynStateTraceEvent> Parse(string capturedStderr)
    {
        ArgumentNullException.ThrowIfNull(capturedStderr);
        List<RoslynStateTraceEvent> events = [];

        foreach (string line in capturedStderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith(ProbeConstants.RoslynStateTracePrefix, StringComparison.Ordinal))
                continue;
            if (line.Length > MaxTraceLineLength)
                throw new InvalidDataException("SETRACE line exceeded the bounded maximum length.");
            if (events.Count >= ProbeConstants.MaxRoslynStateTraceEvents)
                throw new InvalidDataException("SETRACE capture exceeded MaxRoslynStateTraceEvents.");

            Dictionary<string, string> fields = new(StringComparer.Ordinal);
            string payload = line[ProbeConstants.RoslynStateTracePrefix.Length..];
            foreach (string token in payload.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                int separator = token.IndexOf('=');
                if (separator <= 0 || separator == token.Length - 1)
                    throw new InvalidDataException("Malformed SETRACE scalar token.");

                string key = token[..separator];
                string value = token[(separator + 1)..];
                if (key.Length > MaxScalarLength || value.Length > MaxScalarLength)
                    throw new InvalidDataException("SETRACE scalar exceeded the bounded maximum length.");
                if (!fields.TryAdd(key, value))
                    throw new InvalidDataException($"Duplicate SETRACE key: {key}");
            }

            long seq = ParseRequiredLong(fields, "seq");
            string eventName = ParseRequiredString(fields, "event");
            events.Add(new RoslynStateTraceEvent(
                seq,
                eventName,
                ParseOptionalInt(fields, "pid"),
                ParseOptionalInt(fields, "managedThreadId"),
                ParseOptionalInt(fields, "version"),
                ParseOptionalInt(fields, "document"),
                ParseOptionalInt(fields, "solution"),
                ParseOptionalInt(fields, "workspaceSolution"),
                ParseOptionalInt(fields, "selectedSolution"),
                ParseOptionalString(fields, "solutionStateContentVersion"),
                ParseOptionalInt(fields, "project"),
                ParseOptionalInt(fields, "tracker"),
                ParseOptionalString(fields, "trackerState"),
                ParseOptionalInt(fields, "pendingCount"),
                ParseOptionalInt(fields, "compilation"),
                ParseOptionalBool(fields, "tryGetCompilation"),
                ParseOptionalString(fields, "targetHash"),
                ParseOptionalString(fields, "trackedTargetHash"),
                ParseOptionalString(fields, "oldTargetHash"),
                ParseOptionalString(fields, "previousNewTargetHash"),
                ParseOptionalString(fields, "newTargetHash"),
                ParseOptionalString(fields, "returnPath"),
                ParseOptionalString(fields, "forkKind"),
                ParseOptionalString(fields, "firstActionKind")));
        }

        for (int i = 1; i < events.Count; i++)
        {
            if (events[i].Seq <= events[i - 1].Seq)
                throw new InvalidDataException("SETRACE seq values were not strictly increasing.");
        }

        return events;
    }

    private static long ParseRequiredLong(IReadOnlyDictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out string? value) && long.TryParse(value, out long parsed)
            ? parsed
            : throw new InvalidDataException($"Missing or malformed required SETRACE key: {key}");

    private static string ParseRequiredString(IReadOnlyDictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidDataException($"Missing or malformed required SETRACE key: {key}");

    private static int? ParseOptionalInt(IReadOnlyDictionary<string, string> fields, string key)
    {
        if (!fields.TryGetValue(key, out string? value) || value == "<unavailable>")
            return null;
        return int.TryParse(value, out int parsed)
            ? parsed
            : throw new InvalidDataException($"Malformed integer SETRACE key: {key}");
    }

    private static bool? ParseOptionalBool(IReadOnlyDictionary<string, string> fields, string key)
    {
        if (!fields.TryGetValue(key, out string? value) || value == "<unavailable>")
            return null;
        return bool.TryParse(value, out bool parsed)
            ? parsed
            : throw new InvalidDataException($"Malformed boolean SETRACE key: {key}");
    }

    private static string? ParseOptionalString(IReadOnlyDictionary<string, string> fields, string key) =>
        fields.TryGetValue(key, out string? value) && value != "<unavailable>" ? value : null;
}
