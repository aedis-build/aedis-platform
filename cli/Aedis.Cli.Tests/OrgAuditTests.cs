using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Aedis.Cli.Commands;
using Aedis.Cli.Org;
using FluentAssertions;
using Xunit;

namespace Aedis.Cli.Tests;

/// <summary>Avaliação por repositório, montagem do ruleset por nível e o comando ponta-a-ponta contra uma API simulada.</summary>
public sealed class OrgAuditTests
{
    private static readonly string Root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    private const string PinnedWorkflow = """
        name: ci
        on: [push]
        permissions: {}
        jobs:
          commits:
            uses: aedis-build/aedis-platform/.github/workflows/conventional-commits.yml@33c000e3effbd9151212c77aa29c24646ec6e5dc
          build:
            runs-on: ubuntu-latest
            steps:
              - uses: actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1
          coverage:
            uses: aedis-build/aedis-platform/.github/workflows/coverage.yml@33c000e3effbd9151212c77aa29c24646ec6e5dc
          sast:
            uses: aedis-build/aedis-platform/.github/workflows/sast.yml@33c000e3effbd9151212c77aa29c24646ec6e5dc
          image:
            uses: aedis-build/aedis-platform/.github/workflows/build-image.yml@33c000e3effbd9151212c77aa29c24646ec6e5dc  # cosign, sbom, provenance
          dast:
            uses: aedis-build/aedis-platform/.github/workflows/dast.yml@33c000e3effbd9151212c77aa29c24646ec6e5dc
        """;

    private static JsonArray Rules(int level) {
        var rules = new JsonArray { new JsonObject { ["type"] = "deletion" }, new JsonObject { ["type"] = "non_fast_forward" } };
        if (level >= 2) rules.Add(new JsonObject { ["type"] = "required_linear_history" });
        rules.Add(new JsonObject {
            ["type"] = "pull_request",
            ["parameters"] = new JsonObject { ["require_code_owner_review"] = level >= 3, ["require_last_push_approval"] = level >= 3 }
        });
        if (level >= 2)
            rules.Add(new JsonObject {
                ["type"] = "required_status_checks",
                ["parameters"] = new JsonObject { ["required_status_checks"] = new JsonArray(new JsonObject { ["context"] = "ci" }) }
            });
        return rules;
    }

    private static RepoSnapshot Snapshot(int protectionLevel, bool allFiles = true, string workflow = PinnedWorkflow, ScorecardResult? scorecard = null) {
        var files = new Dictionary<string, string>();
        if (allFiles)
            foreach (var f in new[] { "SECURITY.md", "LICENSE", "CONTRIBUTING.md", ".github/dependabot.yml", ".github/rulesets/main.json", "CODEOWNERS" })
                files[f] = "x";
        return new RepoSnapshot {
            Name = "repo", DefaultBranch = "main", Files = files,
            Workflows = new Dictionary<string, string> { ["ci.yml"] = workflow },
            BranchRules = Rules(protectionLevel),
            Scorecard = scorecard
        };
    }

    private static ScorecardResult Gold() => new(9.5, new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) {
        ["Token-Permissions"] = 10, ["Dangerous-Workflow"] = 10, ["Binary-Artifacts"] = 10, ["Branch-Protection"] = 9, ["Code-Review"] = 9,
        ["Pinned-Dependencies"] = 10, ["SAST"] = 10, ["Vulnerabilities"] = 10, ["Signed-Releases"] = 9, ["Dependency-Update-Tool"] = 10, ["Security-Policy"] = 10
    });

    [Fact]
    public void Repositorio_completo_alcanca_L3_e_itens_manuais_nao_bloqueiam() {
        var audit = RepoAuditor.Evaluate(Snapshot(3, scorecard: Gold()), 3, Root);

        audit.Items.Where(i => i.Status == "fail").Should().BeEmpty();
        audit.LevelAchieved.Should().Be(3);
        audit.Items.Where(i => i.Status == "manual").Select(i => i.Id).Should().Contain(["BP-14", "BP-21", "BP-25"]);
    }

    [Fact]
    public void Sem_scorecard_publicado_fica_no_L1_mesmo_com_tudo_mais_em_ordem() {
        var audit = RepoAuditor.Evaluate(Snapshot(3), 3, Root);

        audit.LevelAchieved.Should().Be(1);
        audit.Items.Should().Contain(i => i.Id == "SC-L2" && i.Status == "fail" && i.Detail.Contains("não publicado"));
    }

    [Fact]
    public void Action_sem_sha_e_protecao_fraca_derrubam_o_nivel() {
        var unpinned = PinnedWorkflow.Replace("actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1", "actions/checkout@v4");
        var audit = RepoAuditor.Evaluate(Snapshot(1, workflow: unpinned, scorecard: Gold()), 3, Root);

        audit.LevelAchieved.Should().Be(1);
        audit.Items.Should().Contain(i => i.Id == "BP-12" && i.Status == "fail" && i.Detail.Contains("actions/checkout@v4"));
        audit.Items.Should().Contain(i => i.Id == "BP-07-L2" && i.Status == "fail" && i.Detail.Contains("histórico não linear"));
        audit.Items.Should().Contain(i => i.Id == "BP-07-L3" && i.Status == "fail" && i.Detail.Contains("code owner"));
    }

    [Fact]
    public void Arquivos_ausentes_e_sem_pr_obrigatoria_zeram_o_nivel() {
        var audit = RepoAuditor.Evaluate(new RepoSnapshot { Name = "vazio", DefaultBranch = "main" }, 3, Root);

        audit.LevelAchieved.Should().Be(0);
        audit.Items.Where(i => i.Tier == "passing" && i.Status == "fail").Select(i => i.Id)
            .Should().Contain(["BP-01", "BP-02", "BP-04", "BP-06", "BP-07", "BP-07-L1"]);
    }

    [Fact]
    public void Ruleset_por_nivel_cresce_em_exigencia() {
        var l1 = RulesetGuardrail.Build(1, 0, [], true);
        var l3 = RulesetGuardrail.Build(3, 1, ["ci / build"], false);
        string Types(JsonObject r) => string.Join(",", r["rules"]!.AsArray().Select(x => x!["type"]!.GetValue<string>()));

        Types(l1).Should().Be("deletion,non_fast_forward,pull_request");
        l1["bypass_actors"]!.AsArray().Should().HaveCount(1);
        Types(l3).Should().Be("deletion,non_fast_forward,required_linear_history,pull_request,required_status_checks");
        var pr = l3["rules"]!.AsArray().First(x => x!["type"]!.GetValue<string>() == "pull_request")!["parameters"]!;
        pr["require_code_owner_review"]!.GetValue<bool>().Should().BeTrue();
        pr["required_approving_review_count"]!.GetValue<int>().Should().Be(1);
        l3["bypass_actors"]!.AsArray().Should().BeEmpty();
    }

    /// <summary>API do GitHub e do Scorecard simuladas em memória: um repositório completo e um vazio.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string Body)> Writes { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            var url = request.RequestUri!.ToString();
            if (request.Method != HttpMethod.Get) {
                Writes.Add((request.Method, url, await request.Content!.ReadAsStringAsync(ct)));
                return Json("""{"id": 1}""", HttpStatusCode.Created);
            }

            if (url.Contains("/orgs/acme/repos")) return Json("""[{"name":"bom","default_branch":"main"},{"name":"vazio","default_branch":"main"},{"name":"fork","default_branch":"main","fork":true}]""");
            if (url.Contains("/repos/acme/bom/contents/.github/workflows/ci.yml")) return File(PinnedWorkflow);
            if (url.Contains("/repos/acme/bom/contents/.github/workflows")) return Json("""[{"name":"ci.yml","type":"file"}]""");
            if (url.Contains("/repos/acme/bom/contents/")) return File("conteudo");
            if (url.Contains("/repos/acme/bom/rules/branches/main")) return Json(Rules(3).ToJsonString());
            if (url.Contains("/repos/acme/bom/commits/main")) return Json("""{"sha":"abc"}""");
            if (url.Contains("/repos/acme/bom/commits/abc/check-runs")) return Json("""{"check_runs":[{"name":"ci / build","conclusion":"success"},{"name":"OpenSSF Scorecard","conclusion":"success"},{"name":"ci / lint","conclusion":"failure"}]}""");
            if (url.Contains("/repos/acme/bom/rulesets")) return Json("[]");
            if (url.Contains("api.scorecard.dev/projects/github.com/acme/bom")) return Json("""{"score":9.5,"checks":[{"name":"Branch-Protection","score":9},{"name":"Code-Review","score":9},{"name":"Pinned-Dependencies","score":10},{"name":"SAST","score":10},{"name":"Vulnerabilities","score":10},{"name":"Signed-Releases","score":9},{"name":"Dependency-Update-Tool","score":10},{"name":"Security-Policy","score":10},{"name":"Token-Permissions","score":10},{"name":"Dangerous-Workflow","score":10},{"name":"Binary-Artifacts","score":10}]}""");
            if (url.Contains("/repos/acme/vazio/rules/branches/main")) return Json("[]");
            if (url.Contains("/repos/acme/vazio/rulesets")) return Json("[]");
            if (url.Contains("/repos/acme/vazio/commits/main")) return Json("""{"sha":"def"}""");
            if (url.Contains("/repos/acme/vazio/commits/def/check-runs")) return Json("""{"check_runs":[]}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage File(string text) =>
            Json($$"""{"type":"file","content":"{{Convert.ToBase64String(Encoding.UTF8.GetBytes(text))}}"}""");
    }

    [Fact]
    public async Task Comando_gera_relatorio_aplica_ruleset_e_sai_com_1_enquanto_algum_repo_falta() {
        var fake = new FakeGitHub();
        var outDir = Path.Combine(Path.GetTempPath(), "aedis-org-" + Guid.NewGuid().ToString("N"));
        var root = new System.CommandLine.RootCommand { OrgAuditCommand.Create(() => fake) };

        var exit = await root.Parse(["audit", "--org", "acme", "--level", "3", "--apply", "--required-check", "ci / build", "--out", outDir, "--token", "t"]).InvokeAsync();

        exit.Should().Be(1, "o repositório vazio não alcança o L3");
        var json = JsonNode.Parse(await System.IO.File.ReadAllTextAsync(Path.Combine(outDir, "org-audit-summary.json")))!;
        var repos = json["repositories"]!.AsArray();
        repos.Should().HaveCount(2, "forks ficam de fora");
        repos.First(r => r!["name"]!.GetValue<string>() == "bom")!["levelAchieved"]!.GetValue<int>().Should().Be(3);
        repos.First(r => r!["name"]!.GetValue<string>() == "vazio")!["levelAchieved"]!.GetValue<int>().Should().Be(0);
        fake.Writes.Should().HaveCount(2).And.AllSatisfy(w => w.Method.Should().Be(HttpMethod.Post));
        fake.Writes.First(w => w.Url.Contains("/bom/")).Body.Should().Contain("ci / build").And.NotContain("Scorecard").And.NotContain("ci / lint");
        (await System.IO.File.ReadAllTextAsync(Path.Combine(outDir, "org-audit.md"))).Should().Contain("| bom | L3 |").And.Contain("| vazio | L0 |");
    }

    [Fact]
    public async Task Apply_sem_required_check_nao_escreve_nada_e_lista_os_checks_verdes() {
        var fake = new FakeGitHub();
        var stderr = new StringWriter();
        Console.SetError(stderr);
        var root = new System.CommandLine.RootCommand { OrgAuditCommand.Create(() => fake) };

        var exit = await root.Parse(["audit", "--org", "acme", "--repo", "bom", "--apply", "--out", Path.GetTempPath(), "--token", "t"]).InvokeAsync();

        exit.Should().Be(2);
        fake.Writes.Should().BeEmpty();
        stderr.ToString().Should().Contain("--required-check").And.Contain("ci / build");
    }
}
