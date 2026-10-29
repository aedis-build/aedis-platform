using System.CommandLine;
using Aedis.Cli.Commands;
using Aedis.Cli.Templates;
using Aedis.Cli.Validation;
using FluentAssertions;
using Xunit;

namespace Aedis.Cli.Tests;

/// <summary>
///     O template embarcado do site traz o essencial de um repositório L3 (CI do aedis-ci com language node, release da imagem em tag, Dockerfile
///     distroless, proxy com cabeçalhos, values type site) e sai sem token depois da instanciação.
/// </summary>
public sealed class SiteTemplateTests : IDisposable
{
    private const string Pin = "0123456789abcdef0123456789abcdef01234567 # v1.3.3";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aedis-site-" + Guid.NewGuid().ToString("N"));

    private static readonly IReadOnlyDictionary<string, string> Template = TenantTemplate.ReadEmbedded(SiteTemplate.Prefix);

    [Fact]
    public void Template_embarcado_tem_os_arquivos_essenciais() {
        Template.Keys.Should().Contain([
            "package.json", "pnpm-workspace.yaml", "pnpm-lock.yaml", "next.config.ts", "proxy.ts", "lib/security-headers.ts", "components.json",
            "Dockerfile", "vitest.config.ts", "dast-accept.yaml", "SECURITY.md", "README.md", "deploy/app.yaml",
            ".github/workflows/aedis-ci.yml", ".github/workflows/release.yml", ".github/dependabot.yml"
        ]);
        Template[".github/workflows/aedis-ci.yml"].Should().Contain("language: node").And.Contain("dast-start:").And.NotContain("packages: write");
        Template[".github/workflows/release.yml"].Should().Contain("build-image.yml@").And.Contain("packages: write");
        Template["deploy/app.yaml"].Should().Contain("type: site");
        Template["components.json"].Should().Contain("\"@aedis\"");
    }

    [Fact]
    public void Instanciacao_substitui_nome_org_e_ref_sem_deixar_token() {
        var files = SiteTemplate.Render(Template, "portal", "acme", "abc123 # v1.2.3");

        files.Values.Should().AllSatisfy(content => content.Should().NotContain("__AEDIS_"));
        files["package.json"].Should().Contain("\"name\": \"portal\"");
        files["deploy/app.yaml"].Should().Contain("repository: ghcr.io/acme/portal");
        files[".github/workflows/aedis-ci.yml"].Should().Contain("aedis-ci.yml@abc123 # v1.2.3").And.NotContain("platform-ref");
    }

    public void Dispose() {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Theory]
    [InlineData("static")]
    [InlineData("ssr")]
    public void Alvo_cloudflare_gera_wrangler_e_deploy_sem_dockerfile_nem_chart(string type) {
        var files = SiteTemplate.Render(SiteTemplate.Files("cloudflare", type), "portal", "acme", Pin, "portal.acme.com");

        files.Keys.Should().Contain(["wrangler.jsonc", "package.json", "pnpm-lock.yaml", ".github/workflows/deploy.yml", ".github/workflows/aedis-ci.yml", "README.md"]);
        files.Keys.Should().NotContain(["Dockerfile", ".dockerignore", "deploy/app.yaml", ".github/workflows/release.yml", "scripts/standalone.mjs"]);
        files.Values.Should().AllSatisfy(content => content.Should().NotContain("__AEDIS_"));

        var wrangler = files["wrangler.jsonc"];
        wrangler.Should().Contain("\"name\": \"portal\"").And.Contain("\"workers_dev\": false").And.Contain("\"preview_urls\": false")
            .And.Contain("{ \"pattern\": \"portal.acme.com\", \"custom_domain\": true }")
            .And.Contain($"\"compatibility_date\": \"{SiteTemplate.CompatibilityDate(SiteTemplate.Files("cloudflare", type), DateTime.UtcNow)}\"");
        var deploy = files[".github/workflows/deploy.yml"];
        deploy.Should().Contain($"deploy-site.yml@{Pin}").And.Contain($"type: {type}").And.Contain("url: https://portal.acme.com")
            .And.Contain("CLOUDFLARE_API_TOKEN: ${{ secrets.CLOUDFLARE_API_TOKEN }}").And.NotMatchRegex(@"(?m)^\s+secrets:\s*inherit");
        files[".github/workflows/aedis-ci.yml"].Should().Contain($"aedis-ci.yml@{Pin}").And.Contain("pnpm start > dast-server.log");
        files["package.json"].Should().Contain("\"start\": \"wrangler dev --ip 127.0.0.1 --port 8080\"").And.Contain("\"wrangler\":");
        files[".github/dependabot.yml"].Should().NotContain("docker");

        if (type == "ssr") {
            wrangler.Should().Contain("\"nodejs_compat\"").And.Contain(".open-next/worker.js");
            files.Keys.Should().Contain(["open-next.config.ts", "proxy.ts"]);
            deploy.Should().Contain("rollback-version-id:");
        }
        else {
            wrangler.Should().NotContain("nodejs_compat").And.Contain("\"directory\": \"./out\"");
            files.Keys.Should().NotContain(["open-next.config.ts", "proxy.ts", "test/proxy.test.ts"]);
            files["next.config.ts"].Should().Contain("output: \"export\"");
            files["app/layout.tsx"].Should().NotContain("force-dynamic");
        }
    }

    [Fact]
    public void Compatibility_date_e_o_dia_ate_a_data_do_workerd_do_lockfile() {
        var template = new Dictionary<string, string> { ["pnpm-lock.yaml"] = "  workerd@1.20261012.1:\n" };
        SiteTemplate.CompatibilityDate(template, new DateTime(2026, 10, 29)).Should().Be("2026-10-12");
        SiteTemplate.CompatibilityDate(template, new DateTime(2026, 10, 1)).Should().Be("2026-10-01");
        SiteTemplate.CompatibilityDate(new Dictionary<string, string>(), new DateTime(2026, 10, 29)).Should().Be("2026-10-29");
    }

    [Fact]
    public void Alvo_kubernetes_continua_o_site_do_cluster() =>
        SiteTemplate.Files("kubernetes", "ssr").Keys.Should().Contain(["Dockerfile", "deploy/app.yaml"]).And.NotContain("wrangler.jsonc");

    [Theory]
    [InlineData("static")]
    [InlineData("ssr")]
    public void Projeto_cloudflare_gerado_passa_no_validate_nivel_2(string type) {
        TenantTemplate.WriteTo(_dir, SiteTemplate.Render(SiteTemplate.Files("cloudflare", type), "portal", "acme", Pin, "portal.acme.com"), false);

        Run(ValidateCommand.Create(), "validate", "--repo", _dir, "--out", Path.Combine(_dir, "out"), "--level", "2").Should().Be(0);
        ConformanceChecker.UnreleasedPlatformPins(_dir).Should().BeEmpty();
    }

    [Fact]
    public void Comando_gera_o_site_cloudflare_e_exige_o_dominio() {
        var sha = Pin[..40];
        Run(NewSiteCommand.Create(), "new-site", "portal", "--org", "acme", "--target", "cloudflare", "--type", "static", "--platform-ref", sha, "--out", _dir).Should().Be(2);
        Run(NewSiteCommand.Create(), "new-site", "portal", "--org", "acme", "--target", "cloudflare", "--type", "static", "--domain", "https://x", "--platform-ref", sha, "--out", _dir).Should().Be(2);

        Run(NewSiteCommand.Create(), "new-site", "portal", "--org", "acme", "--target", "cloudflare", "--type", "static", "--domain", "portal.acme.com", "--platform-ref", sha, "--out", _dir).Should().Be(0);
        File.ReadAllText(Path.Combine(_dir, "wrangler.jsonc")).Should().Contain("portal.acme.com");
        File.Exists(Path.Combine(_dir, "Dockerfile")).Should().BeFalse();

        Run(NewSiteCommand.Create(), "new-site", "portal", "--org", "acme", "--platform-ref", sha, "--out", Path.Combine(_dir, "k8s")).Should().Be(0);
        File.Exists(Path.Combine(_dir, "k8s", "Dockerfile")).Should().BeTrue();
    }

    private static int Run(Command command, params string[] args) => new RootCommand { command }.Parse(args).Invoke();
}
