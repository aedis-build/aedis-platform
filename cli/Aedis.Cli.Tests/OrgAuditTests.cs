using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Aedis.Cli.Commands;
using Aedis.Cli.Org;
using FluentAssertions;
using Xunit;

namespace Aedis.Cli.Tests;

/// <summary>Avaliação por repositório, montagem do ruleset por nível e o comando ponta-a-ponta contra uma API simulada.</summary>
[Collection("console")]
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
            uses: ./.github/actions/dast@33c000e3effbd9151212c77aa29c24646ec6e5dc
          verify:
            runs-on: ubuntu-latest
            steps:
              - uses: actions/attest-build-provenance@3d3c42e5aac5ba805825da76410c181273ba90b1
              - run: gh attestation verify app.tgz --owner acme
        """;

    private static Dictionary<string, JsonObject?> PassingSummaries() => new(StringComparer.Ordinal) {
        ["coverage-summary"] = new JsonObject { ["result"] = "pass" },
        ["sast-summary"] = new JsonObject { ["result"] = "pass" },
        ["deps-summary"] = new JsonObject { ["result"] = "pass" },
        ["dast-summary"] = new JsonObject { ["result"] = "pass" }
    };

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
            foreach (var f in new[] { "SECURITY.md", "LICENSE", "CONTRIBUTING.md", ".github/dependabot.yml", ".github/rulesets/main.json", "CODEOWNERS", "REUSE.toml" })
                files[f] = "x";
        return new RepoSnapshot {
            Name = "repo", DefaultBranch = "main", Files = files,
            Workflows = new Dictionary<string, string> { ["ci.yml"] = workflow },
            BranchRules = Rules(protectionLevel),
            Scorecard = scorecard,
            ReleaseAssets = ["app-1.0.0.nupkg", "app-1.0.0.nupkg.sigstore", "sbom.spdx.json"],
            BadgeLevel = "gold",
            Summaries = PassingSummaries()
        };
    }

    private static ScorecardResult Gold() => new(9.5, new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) {
        ["Token-Permissions"] = 10, ["Dangerous-Workflow"] = 10, ["Binary-Artifacts"] = 10, ["Branch-Protection"] = 8, ["Code-Review"] = 10,
        ["Pinned-Dependencies"] = 10, ["SAST"] = 10, ["Vulnerabilities"] = 10, ["Signed-Releases"] = 10, ["Dependency-Update-Tool"] = 10,
        ["Security-Policy"] = 10, ["License"] = 10, ["Fuzzing"] = 10
    });

    [Fact]
    public void BP27_falha_quando_o_Scorecard_nao_reconhece_fuzzing() {
        var checks = new Dictionary<string, double>(Gold().Checks, StringComparer.OrdinalIgnoreCase) { ["Fuzzing"] = 0 };
        var audit = RepoAuditor.Evaluate(Snapshot(3, scorecard: new ScorecardResult(9.2, checks)), 3, Root);

        var item = audit.Items.Single(i => i.Id == "BP-27");
        item.Status.Should().Be("fail");
        item.Detail.Should().Be("Scorecard Fuzzing is 0, expected at least 10");
    }

    [Fact]
    public void L3_exige_todo_check_sob_controle_do_processo_em_10() {
        var checks = new Dictionary<string, double>(Gold().Checks, StringComparer.OrdinalIgnoreCase) { ["Code-Review"] = 9 };
        var audit = RepoAuditor.Evaluate(Snapshot(3, scorecard: new ScorecardResult(9.2, checks)), 3, Root);

        audit.LevelAchieved.Should().Be(2);
        audit.Items.Single(i => i.Id == "SC-L3").Detail.Should().Be("Code-Review 9 < 10");
    }

    [Fact]
    public void Caller_unico_do_aedis_ci_conta_como_analise_estatica_e_conventional_commits() {
        const string caller = """
            name: aedis-ci
            on: [pull_request]
            permissions: {}
            jobs:
              aedis:
                uses: aedis-build/aedis-platform/.github/workflows/aedis-ci.yml@33c000e3effbd9151212c77aa29c24646ec6e5dc
            """;
        var audit = RepoAuditor.Evaluate(Snapshot(3, workflow: caller, scorecard: Gold()), 3, Root);

        audit.Items.Where(i => i.Id is "BP-05" or "BP-15").Should().OnlyContain(i => i.Status == "pass");
    }

    [Fact]
    public void Repositorio_completo_alcanca_L3_e_itens_manuais_nao_bloqueiam() {
        var audit = RepoAuditor.Evaluate(Snapshot(3, scorecard: Gold()), 3, Root);

        audit.Items.Where(i => i.Status == "fail").Should().BeEmpty();
        audit.LevelAchieved.Should().Be(3);
        audit.Items.Where(i => i.Status == "manual").Select(i => i.Id).Should().Contain(["BP-21", "BP-25"]).And.NotContain("BP-27");
        audit.Items.First(i => i.Id == "BP-07").Osps.Should().Equal("AC", "BR");
    }

    [Fact]
    public void Evidencia_ausente_ou_reprovada_derruba_o_nivel_e_sem_release_vale_o_workflow() {
        var semEvidencia = RepoAuditor.Evaluate(new RepoSnapshot {
            Name = "repo", DefaultBranch = "main", Workflows = new Dictionary<string, string> { ["ci.yml"] = PinnedWorkflow },
            BranchRules = Rules(3), Scorecard = Gold(), BadgeLevel = "passing",
            Summaries = new Dictionary<string, JsonObject?>(StringComparer.Ordinal) { ["coverage-summary"] = new JsonObject { ["result"] = "fail" } }
        }, 3, Root);

        semEvidencia.Items.Should().Contain(i => i.Id == "BP-13" && i.Status == "fail" && i.Detail.Contains("result 'fail'"));
        semEvidencia.Items.Should().Contain(i => i.Id == "BP-16" && i.Status == "fail" && i.Detail.Contains("no 'sast-summary' artifact"));
        semEvidencia.Items.Should().Contain(i => i.Id == "BP-11" && i.Status == "pass", "sem release, o workflow com cosign/attest vale como evidência");
        semEvidencia.Items.Should().Contain(i => i.Id == "BP-18" && i.Status == "pass");
        semEvidencia.Items.Should().Contain(i => i.Id == "BP-26" && i.Status == "fail" && i.Detail.Contains("below 'silver'"));
        semEvidencia.Items.Should().Contain(i => i.Id == "BP-23" && i.Status == "pass");

        var semVerificacao = RepoAuditor.Evaluate(Snapshot(3, workflow: PinnedWorkflow.Replace("build-image.yml", "other.yml"), scorecard: Gold()), 3, Root);
        semVerificacao.Items.Should().Contain(i => i.Id == "BP-23" && i.Status == "fail" && i.Detail.Contains("build-image|release-nuget"));
        var releaseSemSbom = RepoAuditor.Evaluate(new RepoSnapshot { Name = "r", DefaultBranch = "main", ReleaseAssets = ["app.nupkg"] }, 3, Root);
        releaseSemSbom.Items.Should().Contain(i => i.Id == "BP-22" && i.Status == "fail" && i.Detail.Contains("latest release has no asset"));
    }

    [Fact]
    public void Sem_scorecard_publicado_fica_no_L1_mesmo_com_tudo_mais_em_ordem() {
        var audit = RepoAuditor.Evaluate(Snapshot(3), 3, Root);

        audit.LevelAchieved.Should().Be(1);
        audit.Items.Should().Contain(i => i.Id == "SC-L2" && i.Status == "fail" && i.Detail.Contains("not published"));
    }

    [Fact]
    public void Action_sem_sha_e_protecao_fraca_derrubam_o_nivel() {
        var unpinned = PinnedWorkflow.Replace("actions/checkout@3d3c42e5aac5ba805825da76410c181273ba90b1", "actions/checkout@v4");
        var audit = RepoAuditor.Evaluate(Snapshot(1, workflow: unpinned, scorecard: Gold()), 3, Root);

        audit.LevelAchieved.Should().Be(1);
        audit.Items.Should().Contain(i => i.Id == "BP-12" && i.Status == "fail" && i.Detail.Contains("actions/checkout@v4"));
        audit.Items.Should().Contain(i => i.Id == "BP-07-L2" && i.Status == "fail" && i.Detail.Contains("non-linear history"));
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
        l3["name"]!.GetValue<string>().Should().Be("main", "o mesmo nome do .github/rulesets/main.json versionado");
        RulesetGuardrail.Build(3, 1, [], true, allRepositories: true)["name"]!.GetValue<string>().Should().Be("aedis-main");
    }

    [Fact]
    public void Checks_obrigatorios_vem_do_ruleset_versionado() {
        RulesetGuardrail.RequiredChecksFrom(System.IO.File.ReadAllText(Path.Combine(Root, "template", "tenant", ".github", "rulesets", "main.json"))).Should().NotBeEmpty();
        RulesetGuardrail.RequiredChecksFrom(RulesetWithChecks).Should().Equal("Conventional Commits", "ci / build");
        RulesetGuardrail.RequiredChecksFrom(null).Should().BeEmpty();
        RulesetGuardrail.RequiredChecksFrom("conteudo").Should().BeEmpty();
        RulesetGuardrail.RequiredChecksFrom("""{"rules":[{"type":"deletion"}]}""").Should().BeEmpty();
    }

    private const string RulesetWithChecks = """
        {"name":"main","rules":[{"type":"deletion"},{"type":"required_status_checks","parameters":{"required_status_checks":[{"context":"Conventional Commits"},{"context":"ci / build"}]}}]}
        """;

    private const string OrgJson = """
        {"login":"acme","two_factor_requirement_enabled":true,"members_can_create_public_repositories":false,
         "dependabot_alerts_enabled_for_new_repositories":true,"dependabot_security_updates_enabled_for_new_repositories":true,
         "secret_scanning_enabled_for_new_repositories":true,"secret_scanning_push_protection_enabled_for_new_repositories":true}
        """;

    [Fact]
    public void Organizacao_configurada_alcanca_L3_e_vazia_fica_no_L0() {
        var full = new OrgSnapshot {
            Name = "acme",
            Organization = JsonNode.Parse(OrgJson)!.AsObject(),
            WorkflowPermissions = new JsonObject { ["default_workflow_permissions"] = "read", ["can_approve_pull_request_reviews"] = false },
            Rulesets = [new JsonObject { ["name"] = "aedis-main", ["enforcement"] = "active" }]
        };

        RepoAuditor.EvaluateOrganization(full, 3, Root).LevelAchieved.Should().Be(3);
        var empty = RepoAuditor.EvaluateOrganization(new OrgSnapshot { Name = "acme" }, 3, Root);
        empty.LevelAchieved.Should().Be(0);
        empty.Items.Should().Contain(i => i.Id == "ORG-01" && i.Status == "fail" && i.Detail.Contains("expected read"));
        empty.Items.Should().Contain(i => i.Id == "ORG-06" && i.Status == "fail");
    }

    [Fact]
    public void Ruleset_de_organizacao_vale_para_todos_os_repositorios_sem_checks() {
        var ruleset = RulesetGuardrail.Build(3, 1, [], true, allRepositories: true);

        ruleset["conditions"]!["repository_name"]!["include"]!.AsArray().Select(x => x!.GetValue<string>()).Should().Equal("~ALL");
        ruleset["rules"]!.AsArray().Should().NotContain(r => r!["type"]!.GetValue<string>() == "required_status_checks");
        RulesetGuardrail.OrganizationSettings(1).ContainsKey("secret_scanning_enabled_for_new_repositories").Should().BeFalse();
        RulesetGuardrail.OrganizationSettings(3)["secret_scanning_push_protection_enabled_for_new_repositories"]!.GetValue<bool>().Should().BeTrue();
    }

    /// <summary>API do GitHub e do Scorecard simuladas em memória: a organização, um repositório completo e um vazio.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string Body)> Writes { get; } = [];

        /// <summary>Rulesets do próprio repositório bom (GET com includes_parents=false).</summary>
        public string BomRulesets { get; init; } = "[]";

        /// <summary>Quando ligado, o GitHub rejeita o ruleset do repositório vazio.</summary>
        public bool RejectVazioRuleset { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            var url = request.RequestUri!.ToString();
            if (request.Method != HttpMethod.Get) {
                Writes.Add((request.Method, url, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
                if (RejectVazioRuleset && url.Contains("/repos/acme/vazio/rulesets")) return Json("""{"message":"Validation Failed"}""", HttpStatusCode.UnprocessableEntity);
                return Json("""{"id": 1}""", HttpStatusCode.Created);
            }

            if (url.Contains("/orgs/acme/repos")) return Json("""[{"name":"bom","default_branch":"main"},{"name":"vazio","default_branch":"main"},{"name":"fork","default_branch":"main","fork":true}]""");
            if (url.EndsWith("/orgs/acme")) return Json(OrgJson);
            if (url.EndsWith("/orgs/acme/actions/permissions/workflow")) return Json("""{"default_workflow_permissions":"read","can_approve_pull_request_reviews":false}""");
            if (url.EndsWith("/orgs/acme/rulesets")) return Json("""[{"id":7,"name":"aedis-main","enforcement":"active"}]""");
            if (url.Contains("/repos/acme/bom/contents/.github/workflows/ci.yml")) return File(PinnedWorkflow);
            if (url.Contains("/repos/acme/bom/contents/.github/workflows")) return Json("""[{"name":"ci.yml","type":"file"}]""");
            if (url.Contains("/repos/acme/bom/contents/.github/rulesets/main.json")) return File(RulesetWithChecks);
            if (url.Contains("/repos/acme/bom/contents/")) return File("conteudo");
            if (url.Contains("/repos/acme/bom/rules/branches/main")) return Json(Rules(3).ToJsonString());
            if (url.Contains("/repos/acme/bom/commits/main")) return Json("""{"sha":"abc"}""");
            if (url.Contains("/repos/acme/bom/commits/abc/check-runs")) return Json("""{"check_runs":[{"name":"ci / build","conclusion":"success"},{"name":"OpenSSF Scorecard","conclusion":"success"},{"name":"ci / lint","conclusion":"failure"}]}""");
            if (url.Contains("/repos/acme/bom/rulesets")) return Json(BomRulesets);
            if (url.Contains("/repos/acme/bom/releases/latest")) return Json("""{"assets":[{"name":"bom.nupkg"},{"name":"bom.nupkg.sigstore"},{"name":"bom.sbom.spdx.json"}]}""");
            if (url.Contains("bestpractices.dev/projects.json?url=https://github.com/acme/bom")) return Json("""[{"id":1,"badge_level":"gold"}]""");
            if (url.Contains("/repos/acme/bom/actions/artifacts?name=")) {
                var name = Uri.UnescapeDataString(url.Split("name=")[1].Split('&')[0]);
                return Json($$"""{"artifacts":[{"id":9,"expired":false,"created_at":"2026-10-18T00:00:00Z","workflow_run":{"head_branch":"main"},"archive_download_url":"https://api.github.com/repos/acme/bom/actions/artifacts/9/{{name}}/zip"}]}""");
            }
            if (url.Contains("/repos/acme/bom/actions/artifacts/9/")) return Zip(url.Split('/')[^2] + ".json", """{"result":"pass"}""");
            if (url.Contains("api.scorecard.dev/projects/github.com/acme/bom")) return Json("""{"score":9.5,"checks":[{"name":"Branch-Protection","score":8},{"name":"Code-Review","score":10},{"name":"Pinned-Dependencies","score":10},{"name":"SAST","score":10},{"name":"Vulnerabilities","score":10},{"name":"Signed-Releases","score":10},{"name":"Dependency-Update-Tool","score":10},{"name":"Security-Policy","score":10},{"name":"Token-Permissions","score":10},{"name":"Dangerous-Workflow","score":10},{"name":"Binary-Artifacts","score":10},{"name":"License","score":10},{"name":"Fuzzing","score":10}]}""");
            if (url.Contains("/repos/acme/vazio/rules/branches/main")) return Json("[]");
            if (url.Contains("/repos/acme/vazio/rulesets")) return Json("[]");
            if (url.Contains("/repos/acme/vazio/commits/main")) return Json("""{"sha":"def"}""");
            if (url.Contains("/repos/acme/vazio/commits/def/check-runs")) return Json("""{"check_runs":[]}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        private static HttpResponseMessage Zip(string entry, string content) {
            var stream = new MemoryStream();
            using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
            using (var writer = new StreamWriter(zip.CreateEntry(entry).Open()))
                writer.Write(content);
            stream.Position = 0;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        }

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
        fake.Writes.Where(w => w.Url.Contains("/repos/") && w.Method == HttpMethod.Post).Should().HaveCount(2);
        fake.Writes.Where(w => w.Method == HttpMethod.Patch && w.Url.EndsWith("/repos/acme/bom")).Should().ContainSingle().Which.Body.Should().Contain("allow_auto_merge");
        fake.Writes.First(w => w.Url.Contains("/bom/")).Body.Should().Contain("ci / build").And.NotContain("Scorecard").And.NotContain("ci / lint");
        fake.Writes.Should().ContainSingle(w => w.Method == HttpMethod.Put && w.Url.EndsWith("/orgs/acme/rulesets/7")).Which.Body.Should().Contain("~ALL").And.NotContain("required_status_checks");
        fake.Writes.Should().ContainSingle(w => w.Method == HttpMethod.Patch && w.Url.EndsWith("/orgs/acme")).Which.Body.Should().Contain("secret_scanning_push_protection_enabled_for_new_repositories");
        fake.Writes.Should().ContainSingle(w => w.Method == HttpMethod.Put && w.Url.EndsWith("/actions/permissions/workflow"));
        json["organization"]!["levelAchieved"]!.GetValue<int>().Should().Be(3);
        json["organization"]!["applied"]!.AsArray().Should().HaveCount(3);
        (await System.IO.File.ReadAllTextAsync(Path.Combine(outDir, "org-audit.md"))).Should().Contain("| bom | L3 |").And.Contain("| vazio | L0 |");
        var html = await System.IO.File.ReadAllTextAsync(Path.Combine(outDir, "org-audit.html"));
        html.Should().StartWith("<!doctype html>").And.Contain("id=\"bom\"").And.Contain("id=\"vazio\"")
            .And.Contain("BP-01").And.Contain("ORG-01").And.Contain("OpenSSF Scorecard").And.Contain("ci / build").And.Contain("<th>OSPS</th>");
        JsonNode.Parse(await System.IO.File.ReadAllTextAsync(Path.Combine(outDir, "badges", "bom.json")))!["message"]!.GetValue<string>().Should().Be("L3");
        JsonNode.Parse(await System.IO.File.ReadAllTextAsync(Path.Combine(outDir, "badge.json")))!["message"]!.GetValue<string>().Should().Be("L0", "a badge da organização é o menor nível");
        repos.First(r => r!["name"]!.GetValue<string>() == "bom")!["items"]!.AsArray().First(i => i!["id"]!.GetValue<string>() == "BP-01")!["osps"]!.AsArray().Should().HaveCount(1);
    }

    [Fact]
    public async Task Apply_sem_required_check_le_os_checks_do_ruleset_versionado_e_protege_repo_sem_arquivo() {
        var fake = new FakeGitHub();
        var stderr = new StringWriter();
        Console.SetError(stderr);
        var root = new System.CommandLine.RootCommand { OrgAuditCommand.Create(() => fake) };

        var exit = await root.Parse(["audit", "--org", "acme", "--apply", "--out", Path.GetTempPath(), "--token", "t"]).InvokeAsync();

        exit.Should().Be(1, "o repositório vazio não alcança o L3, mas recebe o ruleset");
        var bom = JsonNode.Parse(fake.Writes.Single(w => w.Method == HttpMethod.Post && w.Url.EndsWith("/repos/acme/bom/rulesets")).Body)!;
        bom["name"]!.GetValue<string>().Should().Be("main");
        bom.ToJsonString().Should().Contain("Conventional Commits").And.Contain("ci / build");
        var vazio = fake.Writes.Single(w => w.Method == HttpMethod.Post && w.Url.EndsWith("/repos/acme/vazio/rulesets")).Body;
        vazio.Should().Contain("pull_request").And.NotContain("required_status_checks");
        stderr.ToString().Should().Contain("vazio: no required checks");
    }

    [Fact]
    public async Task Ruleset_antigo_aedis_main_do_repositorio_e_renomeado_ou_apagado() {
        var renamed = new FakeGitHub { BomRulesets = """[{"id":3,"name":"aedis-main","source_type":"Repository"}]""" };
        var root = new System.CommandLine.RootCommand { OrgAuditCommand.Create(() => renamed) };
        await root.Parse(["audit", "--org", "acme", "--repo", "bom", "--apply", "--out", Path.GetTempPath(), "--token", "t"]).InvokeAsync();
        renamed.Writes.Should().ContainSingle(w => w.Url.Contains("/repos/acme/bom/rulesets")).Which.Should().Match<(HttpMethod Method, string Url, string Body)>(
            w => w.Method == HttpMethod.Put && w.Url.EndsWith("/rulesets/3") && w.Body.Contains("\"name\":\"main\""));

        var both = new FakeGitHub { BomRulesets = """[{"id":3,"name":"aedis-main"},{"id":4,"name":"main"}]""" };
        root = new System.CommandLine.RootCommand { OrgAuditCommand.Create(() => both) };
        await root.Parse(["audit", "--org", "acme", "--repo", "bom", "--apply", "--out", Path.GetTempPath(), "--token", "t"]).InvokeAsync();
        both.Writes.Where(w => w.Url.Contains("/repos/acme/bom/rulesets")).Select(w => (w.Method, w.Url[w.Url.LastIndexOf('/')..]))
            .Should().Equal((HttpMethod.Delete, "/3"), (HttpMethod.Put, "/4"));
    }

    [Fact]
    public async Task Repositorio_que_rejeita_o_ruleset_nao_impede_os_demais_e_sai_com_2() {
        var fake = new FakeGitHub { RejectVazioRuleset = true };
        Console.SetError(new StringWriter());
        var root = new System.CommandLine.RootCommand { OrgAuditCommand.Create(() => fake) };

        var exit = await root.Parse(["audit", "--org", "acme", "--apply", "--out", Path.GetTempPath(), "--token", "t"]).InvokeAsync();

        exit.Should().Be(2);
        fake.Writes.Should().Contain(w => w.Method == HttpMethod.Post && w.Url.EndsWith("/repos/acme/bom/rulesets"));
        fake.Writes.Should().Contain(w => w.Url.EndsWith("/orgs/acme/rulesets/7"), "os guardrails de organização seguem sendo aplicados");
    }
}
