namespace SystemExplorer.CodeService;

internal enum CompletionSemanticOrigin
{
    Unknown,
    Local,
    CurrentType,
    BaseType,
    OtherUserCode,
    FrameworkOrOther,
}

internal static class CompletionSemanticOriginWire
{
    public static string ToWireValue(CompletionSemanticOrigin origin)
        => origin switch
        {
            CompletionSemanticOrigin.Unknown => "Unknown",
            CompletionSemanticOrigin.Local => "Local",
            CompletionSemanticOrigin.CurrentType => "CurrentType",
            CompletionSemanticOrigin.BaseType => "BaseType",
            CompletionSemanticOrigin.OtherUserCode => "OtherUserCode",
            CompletionSemanticOrigin.FrameworkOrOther => "FrameworkOrOther",
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
}
