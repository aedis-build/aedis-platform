using Aedis.Cli.Validation;
using FluentAssertions;
using Xunit;

namespace Aedis.Cli.Tests;

/// <summary>
///     PLT-22 nos módulos Terraform da organização: do Terraform Registry, só versão exata (as releases são imutáveis);
///     por git, SHA com a tag no comentário. Módulos de fora da organização e pastas de cache não contam.
/// </summary>
public sealed class OrgModulePinTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aedis-org-modules-" + Guid.NewGuid().ToString("N"));

    public OrgModulePinTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private void Write(string path, string content) {
        var full = Path.Combine(_dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static string Module(string source, string? version) => $$"""
        module "zone" {
          source  = "{{source}}"
        {{(version is null ? "" : $"  version = \"{version}\"")}}
          zone_name = "aedis.build"
        }
        """.Replace("\n          ", "\n  ").Replace("\n        ", "\n");

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("= 1.2.3")]
    public void Registry_da_org_com_versao_exata_passa(string version) {
        Write("infra/cloudflare/main.tf", Module("aedis-build/aedis/cloudflare", version));

        ConformanceChecker.UnreleasedPlatformPins(_dir).Should().BeEmpty();
        ConformanceChecker.Check(2, new ValidationResult(), _dir).Controls.Single(c => c.Id == "PLT-22").Status.Should().Be("pass");
    }

    [Theory]
    [InlineData("aedis-build/aedis/cloudflare", "~> 1.0", "version ~> 1.0 (exige versão exata X.Y.Z)")]
    [InlineData("registry.terraform.io/aedis-build/aedis/cloudflare", ">= 1.0.0", "version >= 1.0.0")]
    [InlineData("aedis-build/aedis/cloudflare", null, "version ausente")]
    public void Registry_da_org_com_intervalo_ou_sem_versao_falha(string source, string? version, string expected) {
        Write("infra/cloudflare/main.tf", Module(source, version));

        ConformanceChecker.UnreleasedPlatformPins(_dir).Should().ContainSingle(o => o.StartsWith("infra/cloudflare/main.tf:2 ") && o.Contains(expected));
        ConformanceChecker.Check(2, new ValidationResult(), _dir).Controls.Single(c => c.Id == "PLT-22").Status.Should().Be("fail");
    }

    [Fact]
    public void Git_da_org_exige_sha_e_tag_no_comentario() {
        const string sha = "0123456789abcdef0123456789abcdef01234567";
        Write("ok/main.tf", $"module \"a\" {{\n  source = \"git::https://github.com/aedis-build/terraform-cloudflare-aedis.git?ref={sha}\" # v1.0.0\n}}\n");
        Write("tag/main.tf", "module \"a\" {\n  source = \"git::https://github.com/aedis-build/terraform-cloudflare-aedis.git?ref=v1.0.0\"\n}\n");
        Write("semcomentario/main.tf", $"module \"a\" {{\n  source = \"git::https://github.com/aedis-build/terraform-cloudflare-aedis.git?ref={sha}\"\n}}\n");

        ConformanceChecker.UnreleasedPlatformPins(_dir).Should().BeEquivalentTo([
            "semcomentario/main.tf:2 git::https://github.com/aedis-build/terraform-cloudflare-aedis.git?ref=0123456789abcdef0123456789abcdef01234567 (exige ?ref=<sha> # vX.Y.Z)",
            "tag/main.tf:2 git::https://github.com/aedis-build/terraform-cloudflare-aedis.git?ref=v1.0.0 (exige ?ref=<sha> # vX.Y.Z)"
        ]);
    }

    [Fact]
    public void Modulos_de_fora_da_org_e_pastas_de_cache_nao_contam() {
        Write("infra/main.tf", Module("cloudflare/thing/cloudflare", "~> 2.0") + "\n" + Module("../modules/local", null));
        Write("infra/.terraform/modules/zone/main.tf", Module("aedis-build/aedis/cloudflare", "~> 1.0"));
        Write("node_modules/x/main.tf", Module("aedis-build/aedis/cloudflare", null));

        ConformanceChecker.UnreleasedPlatformPins(_dir).Should().BeEmpty();
    }
}
