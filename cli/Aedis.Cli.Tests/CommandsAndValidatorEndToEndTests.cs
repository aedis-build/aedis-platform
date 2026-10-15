using System.CommandLine;
using Aedis.Cli.Commands;
using Aedis.Cli.Validation;
using FluentAssertions;
using Xunit;

namespace Aedis.Cli.Tests;

/// <summary>
///     Comandos ponta a ponta num diretório temporário (sem helm real): init cria o repo, new-app cria o app, e o
///     validador com helm/kubeconform substituídos produz checagens, manifestos e o sumário; o checker de
///     conformidade lê esses manifestos.
/// </summary>
public sealed class CommandsAndValidatorEndToEndTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aedis-cli-e2e-" + Guid.NewGuid().ToString("N"));

    public CommandsAndValidatorEndToEndTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static int Run(Command command, params string[] args) => new RootCommand { command }.Parse(args).Invoke();

    [Fact]
    public void Init_e_new_app_criam_o_repo_e_o_app() {
        Run(InitCommand.Create(), "init", "--cloud", "aws", "--org", "acme", "--envs", "dev,prod", "--out", _dir).Should().Be(0);
        File.Exists(Path.Combine(_dir, "platform", "values", "prod.yaml")).Should().BeTrue();

        Run(NewAppCommand.Create(), "new-app", "orders-api", "--type", "api", "--namespace", "shop", "--repo", _dir).Should().Be(0);
        File.ReadAllText(Path.Combine(_dir, "apps", "orders-api", "app.yaml")).Should().Contain("repository: ghcr.io/acme/orders-api");
        File.Exists(Path.Combine(_dir, "apps", "orders-api", "env", "dev.yaml")).Should().BeTrue();

        Run(NewAppCommand.Create(), "new-app", "orders-api", "--type", "api", "--namespace", "shop", "--repo", _dir).Should().Be(1, "o app já existe");
        Run(NewAppCommand.Create(), "new-app", "Bad_Name", "--type", "api", "--namespace", "shop", "--repo", _dir).Should().Be(2);
        Run(NewAppCommand.Create(), "new-app", "x", "--type", "job", "--namespace", "shop", "--repo", _dir).Should().Be(2);
    }

    [Fact]
    public async Task Validador_renderiza_valida_e_escreve_o_sumario() {
        Run(InitCommand.Create(), "init", "--cloud", "generic", "--org", "acme", "--envs", "dev,prod", "--out", _dir);
        Run(NewAppCommand.Create(), "new-app", "orders-api", "--type", "api", "--namespace", "shop", "--repo", _dir);
        var validator = new RepoValidator(new StubHelm(), new StubKubeconform());

        var result = await validator.ValidateAsync(_dir, "./chart", null, Path.Combine(_dir, "out"), "1.31.0", CancellationToken.None);

        result.Passed.Should().BeTrue();
        result.Checks.Select(c => c.Check).Should().Contain(["helm-template", "kubeconform"]);
        result.Rendered.Keys.Should().BeEquivalentTo(["orders-api/dev", "orders-api/prod"]);
        File.Exists(Path.Combine(_dir, "out", "validate-summary.json")).Should().BeTrue();

        var l1 = ConformanceChecker.Check(1, result, _dir);
        l1.Failed.Should().Be(0);
        var l2 = ConformanceChecker.Check(2, result, _dir);
        l2.Controls.Should().Contain(c => c.Id == "PLT-08" && c.Status == "fail", "prod sem digest");
        l2.Unknown.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Falha_de_helm_vira_checagem_fail() {
        Run(InitCommand.Create(), "init", "--cloud", "generic", "--org", "acme", "--envs", "dev", "--out", _dir);
        Run(NewAppCommand.Create(), "new-app", "orders-api", "--type", "worker", "--namespace", "shop", "--repo", _dir);
        var validator = new RepoValidator(new StubHelm(fail: true), new StubKubeconform(available: false));

        var result = await validator.ValidateAsync(_dir, "./chart", null, Path.Combine(_dir, "out"), "1.31.0", CancellationToken.None);

        result.Passed.Should().BeFalse();
        result.Checks.Should().ContainSingle(c => c.Check == "helm-template" && c.Status == "fail" && c.Detail.Contains("boom"));
    }

    [Fact]
    public async Task Repo_sem_apps_falha() {
        var result = await new RepoValidator(new StubHelm(), new StubKubeconform()).ValidateAsync(_dir, "./chart", null, Path.Combine(_dir, "out"), "1.31.0", CancellationToken.None);

        result.Checks.Should().ContainSingle(c => c.Check == "apps-present" && c.Status == "fail");
    }

    private sealed class StubHelm(bool fail = false) : HelmRunner
    {
        public override Task<(int ExitCode, string Stdout, string Stderr)> TemplateAsync(string release, string chart, string? chartVersion, IReadOnlyList<string> valueFiles, CancellationToken ct) {
            if (fail) return Task.FromResult((1, "", "boom"));
            var env = Path.GetFileNameWithoutExtension(valueFiles[^1]);
            var cls = env == "prod" ? "prod" : "nonprod";
            var manifest = $$"""
                kind: ServiceAccount
                ---
                kind: ExternalSecret
                ---
                kind: PodDisruptionBudget
                ---
                kind: Rollout
                metadata:
                  labels:
                    app.kubernetes.io/component: api
                    aedis.build/environment-class: {{cls}}
                spec:
                  template:
                    spec:
                      securityContext:
                        runAsNonRoot: true
                        seccompProfile:
                          type: RuntimeDefault
                      containers:
                        - image: ghcr.io/acme/{{release}}:0.0.0
                          securityContext:
                            readOnlyRootFilesystem: true
                            allowPrivilegeEscalation: false
                          readinessProbe:
                            httpGet: {}
                """;
            return Task.FromResult((0, manifest, ""));
        }
    }

    private sealed class StubKubeconform(bool available = true) : KubeconformRunner
    {
        public override bool IsAvailable() => available;

        public override Task<(int ExitCode, string Output)> ValidateAsync(string manifest, string kubernetesVersion, CancellationToken ct) => Task.FromResult((0, "ok"));
    }
}
