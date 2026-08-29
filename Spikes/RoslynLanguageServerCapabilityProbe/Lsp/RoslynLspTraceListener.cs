using System.Diagnostics;
using System.Globalization;
using StreamJsonRpc;

namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Lsp;

internal sealed class RoslynLspTraceListener : TraceListener
{
    private readonly RoslynLspClientCallbacks _callbacks;

    public RoslynLspTraceListener(RoslynLspClientCallbacks callbacks)
    {
        _callbacks = callbacks;
    }

    public override void Write(string? message)
    {
    }

    public override void WriteLine(string? message)
    {
    }

    public override void TraceEvent(
        TraceEventCache? eventCache,
        string source,
        TraceEventType eventType,
        int id,
        string? message)
    {
        if (id == (int)JsonRpc.TraceEvents.RequestWithoutMatchingTarget)
            _callbacks.CaptureUnsupportedServerRequest(message ?? "RequestWithoutMatchingTarget");
    }

    public override void TraceEvent(
        TraceEventCache? eventCache,
        string source,
        TraceEventType eventType,
        int id,
        string? format,
        params object?[]? args)
    {
        if (id != (int)JsonRpc.TraceEvents.RequestWithoutMatchingTarget)
            return;

        string description = format ?? "RequestWithoutMatchingTarget";
        if (format is not null && args is { Length: > 0 })
        {
            try
            {
                description = string.Format(CultureInfo.InvariantCulture, format, args);
            }
            catch (FormatException)
            {
            }
        }

        _callbacks.CaptureUnsupportedServerRequest(description);
    }
}
