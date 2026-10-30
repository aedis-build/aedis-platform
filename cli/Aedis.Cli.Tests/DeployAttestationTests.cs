using Aedis.Cli.Validation;
using FluentAssertions;
using Xunit;

namespace Aedis.Cli.Tests;

/// <summary>
///     PLT-24 (L3): um deploy-site com <c>attestation: false</c> (repositório privado sem Enterprise Cloud) não tem
///     proveniência SLSA Build L3, então o repositório não fecha o L3. Comentários e outros valores não contam.
/// </summary>
public sealed class DeployAttestationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aedis-plt24-" + Guid.NewGuid().ToString("N"));

    public DeployAttestationTests() => Directory.CreateDirectory(Path.Combine(_root, ".github", "workflows"));

    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public void PLT24_aponta_o_deploy_sem_atestacao() {
        File.WriteAllText(Path.Combine(_root, ".github", "workflows", "deploy.yml"), """
            jobs:
              deploy:
                uses: aedis-build/aedis-platform/.github/workflows/deploy-site.yml@0123456789abcdef0123456789abcdef01234567 # v4.1.0
                with:
                  type: ssr
                  # attestation: false fica para repositórios privados
                  attestation: false
                  code-scanning: false
            """);
        File.WriteAllText(Path.Combine(_root, ".github", "workflows", "ok.yml"), """
            jobs:
              deploy:
                with:
                  attestation: true
                  attestation-note: "false"
            """);

        ConformanceChecker.DisabledAttestations(_root).Should().Equal(".github/workflows/deploy.yml:7");
    }

    [Fact]
    public void PLT24_passa_sem_workflows_ou_com_atestacao() {
        ConformanceChecker.DisabledAttestations(Path.Combine(_root, "sem-repo")).Should().BeEmpty();
        File.WriteAllText(Path.Combine(_root, ".github", "workflows", "deploy.yml"), "with:\n  attestation: 'false' # aspas também contam\n");
        ConformanceChecker.DisabledAttestations(_root).Should().Equal(".github/workflows/deploy.yml:2");
    }
}
