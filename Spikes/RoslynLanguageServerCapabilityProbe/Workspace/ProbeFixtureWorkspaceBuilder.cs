namespace SystemExplorer.CodeService.Spikes.RoslynLanguageServerCapabilityProbe.Workspace;

internal static class ProbeFixtureWorkspaceBuilder
{
    public static async Task<ProbeFixtureWorkspace> CreateAsync(bool keepArtifacts, CancellationToken cancellationToken)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "SystemExplorer.CodeService",
            "RoslynProbe",
            "fixture_" + Guid.NewGuid().ToString("N"));
        string projectDirectory = Path.Combine(root, "ProbeFixture");
        Directory.CreateDirectory(projectDirectory);

        string solution = Path.Combine(root, "ProbeFixture.slnx");
        string project = Path.Combine(projectDirectory, "ProbeFixture.csproj");
        string target = Path.Combine(projectDirectory, "ProbeTarget.cs");
        string consumer = Path.Combine(projectDirectory, "ProbeConsumer.cs");
        string diagnostics = Path.Combine(projectDirectory, "ProbeDiagnostics.cs");

        try
        {
            await File.WriteAllTextAsync(solution, SolutionText, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(project, ProjectText, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(target, TargetText, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(consumer, ConsumerText, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(diagnostics, DiagnosticsText, cancellationToken).ConfigureAwait(false);

            return new ProbeFixtureWorkspace(root, solution, project, target, consumer, diagnostics, keepArtifacts);
        }
        catch
        {
            if (!keepArtifacts && Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            throw;
        }
    }

    private const string SolutionText = """
<Solution>
  <Project Path="ProbeFixture/ProbeFixture.csproj" />
</Solution>
""";

    private const string ProjectText = """
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
""";

    private const string TargetText = """
namespace RoslynProbeFixture;

public interface IProbeContract
{
    int ProbeInterfaceMember { get; }
}

public class ProbeBase
{
    public int ProbeBasePublic { get; } = 1;
    protected int ProbeBaseProtected { get; } = 2;
    public virtual int ProbeVirtualMember() => 3;
}

public class ProbeTarget : ProbeBase, IProbeContract
{
    private int ProbePrivateField = 4;

    public int ProbeInterfaceMember => 5;
    public int ProbeInstanceProperty { get; } = 6;
    public static int ProbeStaticProperty { get; } = 7;
    public int ProbeDiskMember { get; } = 8;
    public int ProbeDefinitionSymbol { get; } = 10;
    public override int ProbeVirtualMember() => 9;
    public T ProbeGenericMethod<T>(T value) => value;
    public void ProbeRenameSymbol() { }
    public void ProbeReferenceSymbol() { }

    public void ProbeInsideType()
    {
        _ = this./*PROBE_PRIVATE_COMPLETION*/ProbePrivateField;
        _ = this./*PROBE_GENERIC_COMPLETION*/ProbeGenericMethod(1);
    }
}

public sealed class ProbeDerived : ProbeTarget
{
    public int ProbeDerivedMember()
    {
        return this./*PROBE_DERIVED_COMPLETION*/ProbeBaseProtected;
    }
}

public static class ProbeExtensions
{
    public static int ProbeExtension(this ProbeTarget target) => target.ProbeInstanceProperty;
}
""";

    private const string ConsumerText = """
namespace RoslynProbeFixture;

public sealed class ProbeConsumer
{
    public int InstanceCompletion(ProbeTarget target)
    {
        return target./*PROBE_INSTANCE_COMPLETION*/ProbeInstanceProperty;
    }

    public int StaticCompletion()
    {
        return ProbeTarget./*PROBE_STATIC_COMPLETION*/ProbeStaticProperty;
    }

    public int Definition(ProbeTarget target)
    {
        return target./*PROBE_DEFINITION*/ProbeDefinitionSymbol;
    }

    public void References(ProbeTarget target)
    {
        target./*PROBE_REFERENCES*/ProbeReferenceSymbol();
        target.ProbeReferenceSymbol();
    }

    public void Rename(ProbeTarget target)
    {
        target./*PROBE_RENAME*/ProbeRenameSymbol();
    }

    public int Extension(ProbeTarget target)
    {
        return target.ProbeExtension();
    }
}
""";

    private const string DiagnosticsText = """
namespace RoslynProbeFixture;

public sealed class ProbeDiagnostics
{
    public int Baseline()
    {
        int value = 1;
        return value;
    }
}
""";
}
