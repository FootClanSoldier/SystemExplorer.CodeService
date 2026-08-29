namespace SystemExplorer.CodeService;

internal readonly record struct LocalTransportEndpoint(
    string Scheme,
    string Address,
    int Port);
