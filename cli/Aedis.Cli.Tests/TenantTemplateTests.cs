using Aedis.Cli.Templates;
using FluentAssertions;
using Xunit;

namespace Aedis.Cli.Tests;

/// <summary>
///     O template embarcado do tenant é instanciado por perfil de nuvem: um platform/values por ambiente com a
///     classe certa, tokens todos substituídos e os defaults de cofre/ingress do perfil.
/// </summary>
public sealed class TenantTemplateTests
{
    private static readonly IReadOnlyDictionary<string, string> Template = TenantTemplate.ReadEmbedded();

    [Fact]
    public void Template_embarcado_tem_os_arquivos_essenciais() {
        Template.Keys.Should().Contain(["platform/values/_env.yaml.tmpl", "argocd/applicationset.yaml", ".github/workflows/validate.yml", "CODEOWNERS", "README.md"]);
    }

    [Fact]
    public void Gera_um_values_por_ambiente_com_a_classe_certa() {
        var files = TenantTemplate.Render(Template, new TenantOptions(CloudProvider.Aws, "acme", ["dev", "prod"]));

        files.Should().ContainKey("platform/values/dev.yaml").WhoseValue.Should().Contain("environmentClass: nonprod").And.Contain("provider: aws").And.Contain("preset: alb");
        files.Should().ContainKey("platform/values/prod.yaml").WhoseValue.Should().Contain("environmentClass: prod");
        files.Keys.Should().NotContain("platform/values/_env.yaml.tmpl");
    }

    [Theory]
    [InlineData(CloudProvider.Azure, "azure", "agic")]
    [InlineData(CloudProvider.DigitalOcean, "vault", "do-nginx")]
    [InlineData(CloudProvider.Generic, "vault", "nginx")]
    public void Perfil_define_cofre_e_ingress(CloudProvider cloud, string secrets, string preset) {
        var files = TenantTemplate.Render(Template, new TenantOptions(cloud, "acme", ["dev"]));

        files["platform/values/dev.yaml"].Should().Contain($"provider: {secrets}").And.Contain($"preset: {preset}");
    }

    [Fact]
    public void Nenhum_token_sobra_depois_da_instanciacao() {
        var files = TenantTemplate.Render(Template, new TenantOptions(CloudProvider.Generic, "acme", ["dev", "prod"], "v1", "oci://ghcr.io/aedis-build/charts/aedis-app", "0.1.0"));

        files.Values.Should().AllSatisfy(content => content.Should().NotContain("__AEDIS_"));
        files["CODEOWNERS"].Should().Contain("@acme/platform-security");
        files["argocd/applicationset.yaml"].Should().Contain("targetRevision: 0.1.0");
    }

    [Theory]
    [InlineData("prod", "prod")]
    [InlineData("production", "prod")]
    [InlineData("staging", "nonprod")]
    public void Classe_do_ambiente_vem_do_nome(string env, string expected) =>
        TenantTemplate.EnvironmentClass(env).Should().Be(expected);

    [Fact]
    public void WriteTo_nao_sobrescreve_sem_force() {
        var dir = Path.Combine(Path.GetTempPath(), "aedis-cli-" + Guid.NewGuid().ToString("N"));
        try {
            var files = new Dictionary<string, string> { ["a/b.txt"] = "1" };
            TenantTemplate.WriteTo(dir, files, overwrite: false).Should().Be(1);
            files["a/b.txt"] = "2";
            TenantTemplate.WriteTo(dir, files, overwrite: false).Should().Be(0);
            File.ReadAllText(Path.Combine(dir, "a", "b.txt")).Should().Be("1");
            TenantTemplate.WriteTo(dir, files, overwrite: true).Should().Be(1);
            File.ReadAllText(Path.Combine(dir, "a", "b.txt")).Should().Be("2");
        }
        finally {
            Directory.Delete(dir, true);
        }
    }
}
