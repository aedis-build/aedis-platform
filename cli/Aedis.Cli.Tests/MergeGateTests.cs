using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Aedis.Cli.Commands;
using Aedis.Cli.Org;
using FluentAssertions;
using Xunit;

namespace Aedis.Cli.Tests;

/// <summary>Portão de merge: avaliação pura da política, JWT da App e o comando ponta-a-ponta contra uma API simulada.</summary>
[Collection("console")]
public sealed class MergeGateTests
{
    private const string Policy = """
        schema: aedis.platform/merge-policy/v1
        trustedAuthors: [octo]
        humanRequiredPaths: [".github/**", "SECURITY.md"]
        requiredSummaries: [coverage-summary, sast-summary]
        coverageNoRegression: true
        maxChangedLines: 500
        """;

    private static PullRequestSnapshot Good() => new() {
        Number = 5, NodeId = "PR_1", Author = "octo", HeadSha = "abcdef1234567890", Title = "feat(api): nova rota",
        MergeableState = "blocked", ChangedLines = 120, Files = ["src/Api/Thing.cs", "tests/Api.Tests/ThingTests.cs"],
        CommitMessages = ["feat(api): nova rota"], RequiredChecks = ["ci / build", "sast"],
        CheckConclusions = new Dictionary<string, string?> { ["ci / build"] = "success", ["sast"] = "success" },
        Summaries = new Dictionary<string, JsonObject?> {
            ["coverage-summary"] = new JsonObject { ["result"] = "pass", ["total"] = new JsonObject { ["line"] = 95.5 } },
            ["sast-summary"] = new JsonObject { ["result"] = "pass" }
        },
        BaseCoverage = new JsonObject { ["total"] = new JsonObject { ["line"] = 95.4 } }
    };

    [Fact]
    public void PR_que_cumpre_tudo_e_aprovada() {
        var result = MergeGate.Evaluate(MergePolicy.Parse(Policy), Good());

        result.Decision.Should().Be("approve", string.Join("; ", result.Checks.Where(c => c.Status != "pass").Select(c => c.Id + ":" + c.Detail)));
    }

    [Fact]
    public void Check_pendente_ou_sumario_ausente_espera_e_falhas_bloqueiam() {
        var policy = MergePolicy.Parse(Policy);
        var pending = Good() with { CheckConclusions = new Dictionary<string, string?> { ["ci / build"] = "success", ["sast"] = null } };
        MergeGate.Evaluate(policy, pending).Decision.Should().Be("wait");

        var noSummary = Good() with { Summaries = new Dictionary<string, JsonObject?> { ["coverage-summary"] = Good().Summaries["coverage-summary"] } };
        MergeGate.Evaluate(policy, noSummary).Decision.Should().Be("wait");

        var failAndPending = pending with { Title = "feat!: quebra" };
        MergeGate.Evaluate(policy, failAndPending).Decision.Should().Be("wait", "the verdict waits for every check to finish, even with a fail already known");

        var failing = Good() with { CheckConclusions = new Dictionary<string, string?> { ["ci / build"] = "failure", ["sast"] = "success" } };
        var r = MergeGate.Evaluate(policy, failing);
        r.Decision.Should().Be("block");
        r.Checks.Should().Contain(c => c.Id == "check:ci / build" && c.Status == "fail");
    }

    [Theory]
    [InlineData("author", "stranger", null, null, null, 0)]
    [InlineData("paths", null, ".github/workflows/ci.yml", null, null, 0)]
    [InlineData("title", null, null, "Atualiza coisas", null, 0)]
    [InlineData("trailers", null, null, null, "feat(api): x\n\nCo-Authored-By: Claude <noreply@anthropic.com>", 0)]
    [InlineData("breaking", null, null, "feat(api)!: quebra", null, 0)]
    [InlineData("size", null, null, null, null, 5000)]
    [InlineData("mergeable", null, null, null, null, -1)]
    public void Cada_criterio_bloqueia_sozinho(string id, string? author, string? file, string? title, string? commit, int lines) {
        var pr = Good();
        var snapshot = new PullRequestSnapshot {
            Number = pr.Number, NodeId = pr.NodeId, Author = author ?? pr.Author, HeadSha = pr.HeadSha, Title = title ?? pr.Title,
            MergeableState = lines < 0 ? "dirty" : pr.MergeableState, ChangedLines = lines > 0 ? lines : pr.ChangedLines,
            Files = file is null ? pr.Files : [file], CommitMessages = commit is null ? pr.CommitMessages : [commit],
            RequiredChecks = pr.RequiredChecks, CheckConclusions = pr.CheckConclusions, Summaries = pr.Summaries, BaseCoverage = pr.BaseCoverage
        };

        var result = MergeGate.Evaluate(MergePolicy.Parse(Policy), snapshot);

        result.Decision.Should().Be("block");
        result.Checks.Should().ContainSingle(c => c.Status == "fail").Which.Id.Should().Be(id);
    }

    [Fact]
    public void Regressao_de_cobertura_e_politica_vazia_bloqueiam() {
        var regressed = Good() with { BaseCoverage = new JsonObject { ["total"] = new JsonObject { ["line"] = 96.0 } } };
        MergeGate.Evaluate(MergePolicy.Parse(Policy), regressed).Checks.Should().Contain(c => c.Id == "coverage-regression" && c.Status == "fail");
        MergeGate.Evaluate(MergePolicy.Parse(null), Good()).Checks.Should().Contain(c => c.Id == "author" && c.Detail.Contains("trustedAuthors"));
    }

    [Fact]
    public void App_do_portao_nao_aprova_pr_propria() {
        var policy = MergePolicy.Parse(Policy.Replace("[octo]", "[octo, \"aedis-compliance[bot]\"]"));
        var own = Good() with { Author = "aedis-compliance[bot]" };
        MergeGate.Evaluate(policy, own, "aedis-compliance[bot]").Checks.Should().Contain(c => c.Id == "self-review" && c.Status == "fail");
        MergeGate.Evaluate(policy, Good(), "aedis-compliance[bot]").Checks.Should().Contain(c => c.Id == "self-review" && c.Status == "pass");
        MergeGate.Evaluate(policy, own).Checks.Should().Contain(c => c.Id == "self-review" && c.Status == "pass");
    }

    [Theory]
    [InlineData(".github/**", ".github/workflows/ci.yml", true)]
    [InlineData(".github/**", "src/.github/x", false)]
    [InlineData("**/*Security*/**", "src/Aedis.Security.Web/A.cs", true)]
    [InlineData("apps/*/env/prod.yaml", "apps/orders/env/prod.yaml", true)]
    [InlineData("apps/*/env/prod.yaml", "apps/orders/sub/env/prod.yaml", false)]
    [InlineData("SECURITY.md", "SECURITY.md", true)]
    public void Glob_casa_como_esperado(string glob, string path, bool expected) =>
        MergeGate.GlobMatches(glob, path).Should().Be(expected);

    [Fact]
    public void Comentario_de_aprovacao_e_legivel_e_traz_a_nota_do_auto_merge_sem_json() {
        var result = MergeGate.Evaluate(MergePolicy.Parse(Policy), Good());
        var note = MergeGate.AutoMergeNote("""GitHub rejected enablePullRequestAutoMerge (200): {"errors":[{"message":"Auto-merge is not supported for stacked pull requests."}]}""");

        var comment = MergeGate.ToComment(Good(), result, note);

        comment.Should().StartWith($"<!-- {MergeGate.CommentMarker} `abcdef1` -->")
            .And.Contain("### ✅ Approved by Aedis Compliance")
            .And.Contain("> ℹ️ Approved. Auto-merge turns on when this pull request targets the default branch")
            .And.NotContain("{").And.NotContain("enablePullRequestAutoMerge");
    }

    [Fact]
    public void So_caminho_sensivel_pede_revisao_humana_e_outras_falhas_bloqueiam_com_tabela() {
        var policy = MergePolicy.Parse(Policy);
        var human = Good() with { Files = [".github/workflows/ci.yml"] };
        var humanComment = MergeGate.ToComment(human, MergeGate.Evaluate(policy, human), null);

        humanComment.Should().Contain("### 👀 Needs a maintainer review")
            .And.Contain("| ⛔ | Human-reviewed paths | human review required for: .github/workflows/ci.yml |");

        var failing = Good() with { CheckConclusions = new Dictionary<string, string?> { ["ci / build"] = "failure", ["sast"] = "success" } };
        var failComment = MergeGate.ToComment(failing, MergeGate.Evaluate(policy, failing), null);

        failComment.Should().Contain("### ⛔ Not approved by Aedis Compliance")
            .And.Contain("| ⛔ | Check `ci / build` | concluded 'failure' |")
            .And.NotContain("> ℹ️");
    }

    [Theory]
    [InlineData("Pull request is in clean status", "Approved and ready")]
    [InlineData("Auto merge is not allowed for this repository", "disabled in this repository's settings")]
    [InlineData("something unexpected", "see the merge gate run")]
    public void Erro_do_auto_merge_vira_nota_curta(string error, string expected) =>
        MergeGate.AutoMergeNote(error).Should().Contain(expected);

    [Theory]
    [InlineData("summary:coverage-summary", "Report `coverage-summary`")]
    [InlineData("coverage-regression", "Coverage does not drop")]
    [InlineData("desconhecido", "desconhecido")]
    public void Criterio_tem_nome_legivel(string id, string label) => MergeGate.Label(id).Should().Be(label);

    [Fact]
    public void Jwt_da_App_e_assinado_com_a_chave_e_verificavel_com_a_publica() {
        using var rsa = RSA.Create(2048);
        var jwt = GitHubAppAuth.CreateJwt("12345", rsa.ExportRSAPrivateKeyPem(), new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));
        var parts = jwt.Split('.');

        parts.Should().HaveCount(3);
        Encoding.UTF8.GetString(FromBase64Url(parts[1])).Should().Contain("\"iss\":\"12345\"").And.Contain("\"exp\":");
        rsa.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), FromBase64Url(parts[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).Should().BeTrue();
    }

    private static byte[] FromBase64Url(string s) => Convert.FromBase64String(s.Replace('-', '+').Replace('_', '/').PadRight(s.Length + (4 - s.Length % 4) % 4, '='));

    /// <summary>API simulada: PR 5 pronta; resolução por head SHA; aprovação e auto-merge como App.</summary>
    private sealed class FakeGitHub : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string Auth, string Body)> Writes { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            var url = request.RequestUri!.ToString();
            var auth = request.Headers.Authorization?.Parameter ?? "";
            if (request.Method != HttpMethod.Get) {
                Writes.Add((request.Method, url, auth, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
                if (url.EndsWith("/access_tokens")) return Json("""{"token":"ghs_app"}""", HttpStatusCode.Created);
                if (url.EndsWith("/graphql")) return Json("""{"data":{"enablePullRequestAutoMerge":{"clientMutationId":null}}}""");
                return Json("""{"id": 1}""", HttpStatusCode.Created);
            }

            if (url.Contains("/commits/abcdef1234567890/pulls")) return Json("""[{"number":5,"state":"open","head":{"sha":"abcdef1234567890"}}]""");
            if (url.EndsWith("/pulls/5")) return Json("""{"number":5,"node_id":"PR_1","title":"feat(api): nova rota","body":"","draft":false,"additions":100,"deletions":20,"mergeable_state":"blocked","user":{"login":"octo"},"head":{"sha":"abcdef1234567890","repo":{"full_name":"acme/bom","fork":false}},"base":{"ref":"main"}}""");
            if (url.Contains("/pulls/5/files")) return Json("""[{"filename":"src/Api/Thing.cs"}]""");
            if (url.Contains("/pulls/5/commits")) return Json("""[{"commit":{"message":"feat(api): nova rota"}}]""");
            if (url.Contains("/issues/5/comments")) return Json("[]");
            if (url.Contains("/contents/.github/aedis-merge-policy.yaml")) return Json($$"""{"type":"file","content":"{{Convert.ToBase64String(Encoding.UTF8.GetBytes(Policy))}}"}""");
            if (url.Contains("/rules/branches/main")) return Json("""[{"type":"required_status_checks","parameters":{"required_status_checks":[{"context":"ci / build"}]}}]""");
            if (url.Contains("/commits/abcdef1234567890/check-runs")) return Json("""{"check_runs":[{"name":"ci / build","status":"completed","conclusion":"success","started_at":"2026-10-18T10:00:00Z"}]}""");
            if (url.Contains("/actions/artifacts?name=")) {
                var name = Uri.UnescapeDataString(url.Split("name=")[1].Split('&')[0]);
                return Json($$"""{"artifacts":[{"id":9,"expired":false,"created_at":"2026-10-18T00:00:00Z","workflow_run":{"head_branch":"main","head_sha":"abcdef1234567890"},"archive_download_url":"https://api.github.com/repos/acme/bom/actions/artifacts/9/{{name}}/zip"}]}""");
            }
            if (url.Contains("/actions/artifacts/9/")) return Zip(url.Split('/')[^2] + ".json", """{"result":"pass","total":{"line":95.5}}""");
            if (url.EndsWith("/orgs/acme/installation")) {
                auth.Split('.').Should().HaveCount(3, "a busca da instalação usa o JWT da App");
                return Json("""{"id":77}""");
            }
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
    }

    [Fact]
    public async Task Comando_resolve_a_PR_pelo_head_aprova_como_App_habilita_auto_merge_e_comenta() {
        using var key = RSA.Create(2048);
        var fake = new FakeGitHub();
        var outDir = Path.Combine(Path.GetTempPath(), "aedis-gate-" + Guid.NewGuid().ToString("N"));
        var root = new System.CommandLine.RootCommand { PrGateCommand.Create(() => fake) };
        var stderr = new StringWriter();
        Console.SetError(stderr);

        var exit = await root.Parse(["gate", "--org", "acme", "--repo", "bom", "--head-sha", "abcdef1234567890", "--app-id", "12345", "--app-key", key.ExportRSAPrivateKeyPem(), "--out", outDir, "--token", "t"]).InvokeAsync();

        exit.Should().Be(0, stderr.ToString());
        var summary = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(outDir, "merge-gate-summary.json")))!;
        summary["decision"]!.GetValue<string>().Should().Be("approve");
        summary["action"]!.GetValue<string>().Should().StartWith("Auto-merge is on");
        fake.Writes.Should().ContainSingle(w => w.Url.EndsWith("/pulls/5/reviews")).Which.Should().Match<(HttpMethod, string, string Auth, string Body)>(w => w.Auth == "ghs_app" && w.Body.Contains("APPROVE"));
        fake.Writes.Should().ContainSingle(w => w.Url.EndsWith("/graphql")).Which.Body.Should().Contain("SQUASH").And.Contain("PR_1");
        fake.Writes.Should().ContainSingle(w => w.Url.EndsWith("/issues/5/comments")).Which.Body.Should().Contain(MergeGate.CommentMarker);
    }

    [Fact]
    public async Task Sem_credenciais_da_App_so_avalia_e_registra_que_nao_aprovou() {
        using var key = RSA.Create(2048);
        var fake = new FakeGitHub();
        var outDir = Path.Combine(Path.GetTempPath(), "aedis-gate-" + Guid.NewGuid().ToString("N"));
        var root = new System.CommandLine.RootCommand { PrGateCommand.Create(() => fake) };
        Environment.SetEnvironmentVariable("AEDIS_APP_ID", null);
        Environment.SetEnvironmentVariable("AEDIS_APP_PRIVATE_KEY", null);

        await root.Parse(["gate", "--org", "acme", "--repo", "bom", "--pr", "5", "--out", outDir, "--token", "t"]).InvokeAsync();

        var summary = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(outDir, "merge-gate-summary.json")))!;
        summary["action"]!.GetValue<string>().Should().Contain("GitHub App credentials");
        fake.Writes.Should().NotContain(w => w.Url.EndsWith("/reviews"));
    }
}
