using Aedis.Cli.Commands;
using Aedis.Cli.Validation;
using FluentAssertions;
using Xunit;

namespace Aedis.Cli.Tests;

/// <summary>
///     new-app gera app.yaml/env.yaml coerentes com o chart (prod com digest, não-prod com canário curto) e
///     sugere secrets.keys a partir do appsettings.json; o validador lê o chart do workflow do tenant e serializa
///     o sumário no contrato v1.
/// </summary>
public sealed class NewAppAndValidatorTests
{
    [Fact]
    public void App_yaml_de_api_traz_ingress_desligado_e_secrets_vazios() {
        var yaml = NewAppCommand.RenderAppYaml("orders-api", "api", "shop", "ghcr.io/acme/orders-api", null, []);

        yaml.Should().Contain("name: orders-api").And.Contain("type: api").And.Contain("ingress:").And.Contain("keys: []");
    }

    [Fact]
    public void App_yaml_de_site_traz_ingress_uid_distroless_e_nenhum_secret() {
        var yaml = NewAppCommand.RenderAppYaml("portal", "site", "web", "ghcr.io/acme/portal", null, []);

        yaml.Should().Contain("type: site").And.Contain("ingress:").And.Contain("uid: 65532").And.Contain("/app/.next/cache").And.NotContain("secrets:");
    }

    [Fact]
    public void App_yaml_de_cronjob_traz_schedule() {
        var yaml = NewAppCommand.RenderAppYaml("job", "cronjob", "fin", "ghcr.io/acme/job", "*/5 * * * *", []);

        yaml.Should().Contain("schedule: \"*/5 * * * *\"");
    }

    [Fact]
    public void Env_de_prod_pede_digest_e_nao_prod_tem_canario_curto() {
        NewAppCommand.RenderEnvYaml("prod").Should().Contain("digest:").And.NotContain("setWeight");
        NewAppCommand.RenderEnvYaml("dev").Should().Contain("tag: 0.0.0-rc.0").And.Contain("setWeight: 50");
    }

    [Fact]
    public void Sugere_chaves_sensiveis_do_appsettings() {
        var dir = Path.Combine(Path.GetTempPath(), "aedis-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            File.WriteAllText(Path.Combine(dir, "appsettings.json"), """{"Database":{"ConnectionString":"x","CommandTimeout":30},"Auth":{"ClientSecret":"y"},"Logging":{"Level":"Debug"}}""");

            var keys = NewAppCommand.SuggestSecretKeys(dir, "orders-api");

            keys.Select(k => k.Name).Should().BeEquivalentTo(["Database__ConnectionString", "Auth__ClientSecret"]);
        }
        finally {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Ambientes_vem_de_platform_values_ignorando_modelos() {
        var dir = Path.Combine(Path.GetTempPath(), "aedis-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "platform", "values"));
        try {
            foreach (var f in new[] { "dev.yaml", "prod.yaml", "_env.yaml.tmpl" })
                File.WriteAllText(Path.Combine(dir, "platform", "values", f), "");

            NewAppCommand.DiscoverEnvironments(dir).Should().Equal("dev", "prod");
        }
        finally {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Chart_vem_do_workflow_do_tenant_quando_nao_informado() {
        var dir = Path.Combine(Path.GetTempPath(), "aedis-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, ".github", "workflows"));
        try {
            File.WriteAllText(Path.Combine(dir, ".github", "workflows", "validate.yml"), "with:\n  chart: oci://ghcr.io/acme/charts/aedis-app\n  chart-version: \"0.2.0\"\n");

            RepoValidator.ResolveChart(dir, null, null).Should().Be(("oci://ghcr.io/acme/charts/aedis-app", "0.2.0"));
            RepoValidator.ResolveChart(dir, "./charts/aedis-app", null).Should().Be(("./charts/aedis-app", (string?)null));
        }
        finally {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Sumario_segue_o_contrato_v1() {
        var result = new ValidationResult();
        result.Checks.Add(new ValidationCheck("orders-api", "dev", "helm-template", "pass", ""));
        result.Checks.Add(new ValidationCheck("orders-api", "prod", "kubeconform", "fail", "x"));

        result.Passed.Should().BeFalse();
        result.ToJson().Should().Contain("\"schema\": \"aedis.platform/validate-summary/v1\"").And.Contain("\"result\": \"fail\"");
    }

    [Fact]
    public void Niveis_embarcados_tem_tres_niveis_com_controles() {
        var levels = ConformanceChecker.LoadLevels(Path.GetTempPath());

        levels.Select(l => l.Id).Should().Equal(1, 2, 3);
        levels.Should().AllSatisfy(l => l.Controls.Should().NotBeEmpty());
    }
}
