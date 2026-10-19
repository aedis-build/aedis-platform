using Aedis.Cli.Templates;
using FluentAssertions;
using Xunit;

namespace Aedis.Cli.Tests;

/// <summary>
///     O template embarcado do site traz o essencial de um repositório L3 (CI do app-ci com language node, Dockerfile
///     distroless, proxy com cabeçalhos, values type site) e sai sem token depois da instanciação.
/// </summary>
public sealed class SiteTemplateTests
{
    private static readonly IReadOnlyDictionary<string, string> Template = TenantTemplate.ReadEmbedded(SiteTemplate.Prefix);

    [Fact]
    public void Template_embarcado_tem_os_arquivos_essenciais() {
        Template.Keys.Should().Contain([
            "package.json", "pnpm-workspace.yaml", "pnpm-lock.yaml", "next.config.ts", "proxy.ts", "lib/security-headers.ts", "components.json",
            "Dockerfile", "vitest.config.ts", "dast-accept.yaml", "SECURITY.md", "README.md", "deploy/app.yaml",
            ".github/workflows/ci.yml", ".github/dependabot.yml"
        ]);
        Template[".github/workflows/ci.yml"].Should().Contain("language: node").And.Contain("dast-start:");
        Template["deploy/app.yaml"].Should().Contain("type: site");
        Template["components.json"].Should().Contain("\"@aedis\"");
    }

    [Fact]
    public void Instanciacao_substitui_nome_org_e_ref_sem_deixar_token() {
        var files = SiteTemplate.Render(Template, "portal", "acme", "abc123");

        files.Values.Should().AllSatisfy(content => content.Should().NotContain("__AEDIS_"));
        files["package.json"].Should().Contain("\"name\": \"portal\"");
        files["deploy/app.yaml"].Should().Contain("repository: ghcr.io/acme/portal");
        files[".github/workflows/ci.yml"].Should().Contain("app-ci.yml@abc123").And.Contain("platform-ref: abc123");
    }
}
