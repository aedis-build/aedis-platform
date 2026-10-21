using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Aedis.Cli.Commands;
using Aedis.Cli.Org;
using FluentAssertions;
using Xunit;

namespace Aedis.Cli.Tests;

/// <summary>
///     Identidade própria dos agentes: o token da App do agente e a regra do portão que aceita a aprovação de um
///     mantenedor humano como cumprimento da revisão humana exigida pela política.
/// </summary>
[Collection("console")]
public sealed class AgentIdentityTests
{
    private const string Head = "abcdef1234567890";

    private static JsonObject Review(string login, string state, string commit = Head, string type = "User", string association = "OWNER") =>
        new() {
            ["user"] = new JsonObject { ["login"] = login, ["type"] = type },
            ["state"] = state, ["commit_id"] = commit, ["author_association"] = association
        };

    [Fact]
    public void Aprovacao_valida_e_a_do_mantenedor_humano_no_head_atual() {
        var reviews = new JsonArray(
            Review("owner", "APPROVED"),
            Review("aedis-compliance[bot]", "APPROVED", type: "Bot"),
            Review("aedis-agent-coder[bot]", "APPROVED", type: "Bot"),
            Review("antigo", "APPROVED", commit: "0000000"),
            Review("visitante", "APPROVED", association: "NONE"),
            Review("arrependido", "APPROVED"),
            Review("arrependido", "CHANGES_REQUESTED"),
            Review("comentarista", "COMMENTED"));

        MergeGate.HumanApprovers(reviews, "aedis-agent-coder[bot]", Head).Should().Equal("owner");
        MergeGate.HumanApprovers(new JsonArray(Review("owner", "APPROVED")), "owner", Head).Should().BeEmpty("the author never reviews their own change");
    }

    [Fact]
    public void Caminho_sensivel_aprovado_por_mantenedor_libera_o_portao_para_a_segunda_aprovacao() {
        var policy = MergePolicy.Parse("""
            schema: aedis.platform/merge-policy/v1
            trustedAuthors: ["aedis-agent-coder[bot]"]
            humanRequiredPaths: [".github/**"]
            requiredSummaries: []
            """);
        var pr = new PullRequestSnapshot {
            Number = 7, Author = "aedis-agent-coder[bot]", HeadSha = Head, Title = "ci: ajusta o workflow",
            MergeableState = "blocked", Files = [".github/workflows/ci.yml"], CommitMessages = ["ci: ajusta o workflow"]
        };

        MergeGate.Evaluate(policy, pr).Decision.Should().Be("block");
        MergeGate.Evaluate(policy, pr with { HumanApprovers = ["owner"] }).Decision.Should().Be("approve");
    }

    [Fact]
    public void Com_time_de_mantenedores_so_membros_do_time_contam() {
        var reviews = new JsonArray(Review("owner", "APPROVED"), Review("colaborador", "APPROVED", association: "COLLABORATOR"));

        MergeGate.HumanApprovers(reviews, "aedis-agent-coder[bot]", Head, maintainers: ["Colaborador"]).Should().Equal("colaborador");
        MergeGate.HumanApprovers(reviews, "aedis-agent-coder[bot]", Head, maintainers: []).Should().BeEmpty();
    }

    [Fact]
    public void Time_ilegivel_bloqueia_mudanca_sensivel_em_vez_de_afrouxar_a_regra() {
        var policy = MergePolicy.Parse("""
            trustedAuthors: ["aedis-agent-coder[bot]"]
            humanRequiredPaths: [".github/**"]
            requiredSummaries: []
            maintainersTeam: maintainers
            """);
        var pr = new PullRequestSnapshot {
            Number = 7, Author = "aedis-agent-coder[bot]", HeadSha = Head, Title = "ci: ajusta o workflow", MergeableState = "blocked",
            MaintainersTeamError = "cannot read the members of @acme/maintainers"
        };

        policy.MaintainersTeam.Should().Be("maintainers");
        MergeGate.Evaluate(policy, pr).Decision.Should().Be("approve", "a change outside sensitive paths does not need the team");
        var sensitive = MergeGate.Evaluate(policy, pr with { Files = [".github/workflows/ci.yml"] });
        sensitive.Decision.Should().Be("block");
        sensitive.Checks.Should().Contain(c => c.Id == "maintainers-team" && c.Status == "fail");
        MergeGate.Label("maintainers-team").Should().Be("Maintainers team");
    }

    private sealed class FakeAppApi : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            var url = request.RequestUri!.ToString();
            request.Headers.Authorization!.Parameter!.Split('.').Should().HaveCount(3, "the app authenticates with its JWT");
            var body = url.EndsWith("/orgs/acme/installation") ? """{"id":77}"""
                : url.EndsWith("/app/installations/77/access_tokens") ? """{"token":"ghs_agent"}"""
                : null;
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task Comando_imprime_so_o_token_da_instalacao_do_agente() {
        using var key = RSA.Create(2048);
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        var root = new System.CommandLine.RootCommand { AgentTokenCommand.Create(() => new FakeAppApi()) };

        var exit = await root.Parse(["token", "--org", "acme", "--app-id", "5403201", "--key", key.ExportRSAPrivateKeyPem()]).InvokeAsync();

        exit.Should().Be(0);
        stdout.ToString().Should().Be("ghs_agent");
    }

    [Fact]
    public async Task Sem_app_id_ou_chave_sai_com_2_e_explica() {
        Environment.SetEnvironmentVariable("AEDIS_AGENT_APP_ID", null);
        Environment.SetEnvironmentVariable("AEDIS_AGENT_PRIVATE_KEY", null);
        var stderr = new StringWriter();
        Console.SetError(stderr);
        var root = new System.CommandLine.RootCommand { AgentTokenCommand.Create(() => new FakeAppApi()) };

        var exit = await root.Parse(["token", "--org", "acme"]).InvokeAsync();

        exit.Should().Be(2);
        stderr.ToString().Should().Contain("AEDIS_AGENT_APP_ID");
    }
}
