using Aedis.Cli.Validation;
using FluentAssertions;
using Xunit;

namespace Aedis.Cli.Tests;

/// <summary>
///     PLT-20 (<c>waf-declared-prod</c>) num site na Cloudflare: o <c>wrangler.jsonc</c> de produção desliga
///     <c>workers_dev</c> e <c>preview_urls</c>, todo domínio próprio dele pertence a um workload do módulo
///     <c>infra/cloudflare</c> e as rotas de formulário do site estão no <c>forms</c> desse workload. Fixture boa e
///     variações ruins num diretório temporário.
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
            "prd": { "routes": [{ "pattern": "rlfn.studio", "custom_domain": true }] },
            "preview": { "preview_urls": true }
          }
        }
        """;

    private const string Module = """
        # infra da zona
        module "zone" {
          source = "git::https://github.com/aedis-build/aedis-platform.git//infra/cloudflare?ref=0123456789abcdef0123456789abcdef01234567" # v1.7.0

          zone_name = "rlfn.studio"
          workloads = {
            site = {
              type      = "site-ssr"
              hostnames = ["rlfn.studio", "staging.rlfn.studio"]
              forms     = ["/api/proposals"]
              turnstile = true
              rules = [{
                description = "métodos { estranhos }"
                expression  = "not http.request.method in {\"GET\" \"HEAD\"} and http.request.uri.path eq \"/x\""
                action      = "block"
              }]
              headers = { "X-Extra" = "}" }
            }
            "cdn" = { type = "cdn", hostnames = ["cdn.rlfn.studio"], bucket = "media", allowed_referers = ["rlfn.studio"] }
          }
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
    public void Workloads_sao_lidos_com_chaves_dentro_de_strings_e_chave_entre_aspas() =>
        CloudflareWaf.DeclaredWorkloads(_dir).Should().BeEquivalentTo([
            new CloudflareWorkload("site", ["rlfn.studio", "staging.rlfn.studio"], ["/api/proposals"]),
            new CloudflareWorkload("cdn", ["cdn.rlfn.studio"], [])
        ]);

    [Fact]
    public void Tf_sem_fechar_as_chaves_nao_quebra_a_leitura() {
        Write("infra/cloudflare/main.tf", """module "zone" { source = "x//infra/cloudflare" workloads = { a = { hostnames = ["rlfn.studio"] """);

        CloudflareWaf.DeclaredWorkloads(_dir).Should().ContainSingle(w => w.Name == "a" && w.Hostnames.SequenceEqual(new[] { "rlfn.studio" }));
    }

    [Fact]
    public void Rota_de_formulario_fora_do_forms_do_workload_falha() {
        Write("apps/web/src/app/(site)/contato/route.ts", "const POST = () => new Response(null);\nexport { POST };");

        Plt20().Should().Match<ControlResult>(c => c.Status == "fail"
            && c.Detail.Contains("apps/web/wrangler.jsonc: rota de formulário /contato fora de forms do workload site (rlfn.studio, staging.rlfn.studio)"));
    }

    [Fact]
    public void Dominio_proprio_sem_workload_falha_com_arquivo_env_e_hostname() {
        Write("apps/web/wrangler.jsonc", Wrangler.Replace("\"pattern\": \"rlfn.studio\"", "\"pattern\": \"www.rlfn.studio\""));

        Plt20().Detail.Should().Contain("apps/web/wrangler.jsonc (env.prd): domínio www.rlfn.studio não pertence a nenhum workload de infra/cloudflare");
    }

    [Fact]
    public void Sem_modulo_ou_sem_workloads_falha() {
        Write("infra/cloudflare/main.tf", """module "zone" { source = "../infra/cloudflare" zone_name = "rlfn.studio" }""");
        Plt20().Detail.Should().Contain("sem módulo infra/cloudflare com workloads");

        Write("infra/cloudflare/main.tf", """module "outro" { source = "./x" workloads = { a = { hostnames = ["rlfn.studio"] } } }""");
        Plt20().Detail.Should().Contain("sem módulo infra/cloudflare com workloads");

        Directory.Delete(Path.Combine(_dir, "infra"), true);
        Plt20().Detail.Should().Contain("sem módulo infra/cloudflare com workloads");
    }

    [Fact]
    public void Workload_sem_forms_cobre_um_site_sem_formulario() {
        File.Delete(Path.Combine(_dir, "apps", "web", "src", "app", "api", "proposals", "route.ts"));
        Write("infra/cloudflare/main.tf", Module.Replace("""      forms     = ["/api/proposals"]""", ""));

        Plt20().Status.Should().Be("pass");
    }

    [Theory]
    [InlineData("\"workers_dev\": false", "\"workers_dev\": true", "apps/web/wrangler.jsonc: workers_dev precisa ser false")]
    [InlineData("\"preview_urls\": false,", "", "apps/web/wrangler.jsonc: preview_urls precisa ser false")]
    [InlineData("\"custom_domain\": true }],", "\"custom_domain\": true }], \"workers_dev\": true,", "apps/web/wrangler.jsonc (env.staging): workers_dev precisa ser false")]
    public void Wrangler_de_producao_sem_workers_dev_e_preview_urls_desligados_falha(string from, string to, string expected) {
        Write("apps/web/wrangler.jsonc", Wrangler.Replace(from, to));

        Plt20().Should().Match<ControlResult>(c => c.Status == "fail" && c.Detail.Contains(expected));
    }

    [Fact]
    public void Wrangler_na_raiz_sem_apps_tambem_e_avaliado() {
        Directory.Delete(Path.Combine(_dir, "apps"), true);
        Write("wrangler.jsonc", """{ "name": "site", "routes": [{ "pattern": "rlfn.studio", "custom_domain": true }], "workers_dev": true, "preview_urls": false }""");

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
        CloudflareWaf.DeclaredWorkloads(_dir).Should().BeNull();
    }
}
