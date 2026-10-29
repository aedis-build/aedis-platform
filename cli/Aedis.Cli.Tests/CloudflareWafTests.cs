using Aedis.Cli.Validation;
using FluentAssertions;
using Xunit;

namespace Aedis.Cli.Tests;

/// <summary>
///     PLT-20 (<c>waf-declared-prod</c>) num site na Cloudflare: o <c>wrangler.jsonc</c> de produção desliga
///     <c>workers_dev</c> e <c>preview_urls</c>, e o módulo <c>infra/cloudflare</c> declara o rate limit de toda rota de
///     formulário (ou <c>form_paths = []</c>). Fixture boa e variações ruins num diretório temporário.
/// </summary>
public sealed class CloudflareWafTests : IDisposable
{
    private const string Wrangler = """
        // Worker do OpenNext
        {
          "name": "rlfn-studio",
          "main": ".open-next/worker.js",
          "workers_dev": false,
          "preview_urls": false,
          "env": {
            "staging": { "routes": [{ "pattern": "staging.rlfn.studio", "custom_domain": true }], },
            "preview": { "preview_urls": true }
          }
        }
        """;

    private const string Module = """
        # infra da zona
        module "zone" {
          source = "git::https://github.com/aedis-build/aedis-platform.git//infra/cloudflare?ref=0123456789abcdef0123456789abcdef01234567" # v1.4.0

          zone_name  = "rlfn.studio"
          hostnames  = ["rlfn.studio"]
          form_paths = ["/api/proposals"]
          email_routing = { addresses = [var.inbox] }
        }
        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aedis-cf-waf-" + Guid.NewGuid().ToString("N"));

    public CloudflareWafTests() {
        Write("apps/web/wrangler.jsonc", Wrangler);
        Write("apps/web/src/app/api/proposals/route.ts", "export async function POST(request: Request) { return new Response(null); }");
        Write("apps/web/src/app/api/health/route.ts", "export function GET() { return Response.json({ ok: true }); }");
        Write("infra/cloudflare/main.tf", Module);
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private void Write(string path, string content) {
        var full = Path.Combine(_dir, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private ControlResult Plt20() => ConformanceChecker.Check(3, new ValidationResult(), _dir).Controls.Single(c => c.Id == "PLT-20");

    [Fact]
    public void Fixture_boa_passa() {
        CloudflareWaf.Problems(_dir).Should().BeEmpty();
        CloudflareWaf.FormRoutes(Path.Combine(_dir, "apps", "web")).Should().Equal("/api/proposals");
        Plt20().Status.Should().Be("pass");
    }

    [Fact]
    public void Rota_de_formulario_fora_do_form_paths_falha() {
        Write("apps/web/src/app/(site)/contato/route.ts", "const POST = () => new Response(null);\nexport { POST };");

        Plt20().Should().Match<ControlResult>(c => c.Status == "fail" && c.Detail.Contains("rota de formulário /contato fora do form_paths"));
    }

    [Fact]
    public void Sem_modulo_ou_sem_form_paths_falha() {
        Write("infra/cloudflare/main.tf", Module.Replace("""  form_paths = ["/api/proposals"]""", ""));
        Plt20().Detail.Should().Contain("sem módulo infra/cloudflare declarado");

        Write("infra/cloudflare/main.tf", """module "outro" { source = "./x" form_paths = [] }""");
        Plt20().Detail.Should().Contain("sem módulo infra/cloudflare declarado");

        File.Delete(Path.Combine(_dir, "infra", "cloudflare", "main.tf"));
        Directory.Delete(Path.Combine(_dir, "infra"), true);
        Plt20().Detail.Should().Contain("sem módulo infra/cloudflare declarado");
    }

    [Fact]
    public void Form_paths_vazio_explicito_cobre_um_site_sem_formulario() {
        File.Delete(Path.Combine(_dir, "apps", "web", "src", "app", "api", "proposals", "route.ts"));
        Write("infra/cloudflare/main.tf", Module.Replace("""["/api/proposals"]""", "[]"));

        Plt20().Status.Should().Be("pass");
    }

    [Theory]
    [InlineData("\"workers_dev\": false", "\"workers_dev\": true", "apps/web/wrangler.jsonc: workers_dev precisa ser false")]
    [InlineData("\"preview_urls\": false,", "", "apps/web/wrangler.jsonc: preview_urls precisa ser false")]
    [InlineData("\"custom_domain\": true }]", "\"custom_domain\": true }], \"workers_dev\": true", "apps/web/wrangler.jsonc (env.staging): workers_dev precisa ser false")]
    public void Wrangler_de_producao_sem_workers_dev_e_preview_urls_desligados_falha(string from, string to, string expected) {
        Write("apps/web/wrangler.jsonc", Wrangler.Replace(from, to));

        Plt20().Should().Match<ControlResult>(c => c.Status == "fail" && c.Detail.Contains(expected));
    }

    [Fact]
    public void Wrangler_na_raiz_sem_apps_tambem_e_avaliado() {
        Directory.Delete(Path.Combine(_dir, "apps"), true);
        Write("wrangler.jsonc", """{ "name": "aedis-build", "routes": [{ "pattern": "aedis.build", "custom_domain": true }], "workers_dev": true, "preview_urls": false }""");
        Write("infra/cloudflare/main.tf", Module.Replace("""["/api/proposals"]""", "[]"));

        var conformance = ConformanceChecker.Check(3, new ValidationResult(), _dir);

        conformance.Controls.Single(c => c.Id == "PLT-20").Detail.Should().Be("wrangler.jsonc: workers_dev precisa ser false");
        conformance.Controls.Single(c => c.Id == "PLT-01").Status.Should().Be("unknown", "sem apps/ os controles do cluster não se aplicam");
    }

    [Fact]
    public void Sem_wrangler_de_producao_nada_e_exigido() {
        Write("apps/web/wrangler.jsonc", """{ "name": "dev-only", "routes": [] }""");
        Directory.Delete(Path.Combine(_dir, "infra"), true);

        CloudflareWaf.ProductionWranglers(_dir).Should().BeEmpty();
        CloudflareWaf.Problems(_dir).Should().BeEmpty();
        CloudflareWaf.DeclaredFormPaths(_dir).Should().BeNull();
    }
}
