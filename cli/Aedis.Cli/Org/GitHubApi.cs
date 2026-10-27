using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aedis.Cli.Org;

/// <summary>
///     Cliente mínimo da API REST do GitHub e da API pública do OpenSSF Scorecard, usado pelo
///     <c>aedis org audit</c>. Autentica com <c>GITHUB_TOKEN</c>/<c>GH_TOKEN</c> ou, na falta deles, com o
///     token da sessão do <c>gh</c>.
/// </summary>
public sealed class GitHubApi
{
    private readonly HttpClient _http;

    /// <summary>Cria o cliente sobre um <see cref="HttpClient" /> já configurado (testes) ou um novo com o token.</summary>
    public GitHubApi(HttpClient http, string? token) {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(60);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("aedis-cli");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrWhiteSpace(token))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>Token do ambiente (<c>GITHUB_TOKEN</c>, <c>GH_TOKEN</c>) ou da sessão do <c>gh</c>; nulo se não houver.</summary>
    public static string? ResolveToken() {
        var fromEnv = Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? Environment.GetEnvironmentVariable("GH_TOKEN");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;
        try {
            using var gh = Process.Start(new ProcessStartInfo("gh", "auth token") {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            });
            var output = gh?.StandardOutput.ReadToEnd().Trim();
            gh?.WaitForExit(5000);
            return string.IsNullOrWhiteSpace(output) ? null : output;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) {
            return null;
        }
    }

    /// <summary>Repositórios da organização (sem forks e arquivados), com o nome do branch padrão.</summary>
    public async Task<IReadOnlyList<(string Name, string DefaultBranch)>> ListRepositoriesAsync(string org, CancellationToken ct) {
        var result = new List<(string, string)>();
        for (var page = 1; ; page++) {
            var array = await GetJsonAsync<JsonArray>($"https://api.github.com/orgs/{org}/repos?per_page=100&type=all&page={page}", ct)
                        ?? throw new InvalidOperationException($"Organização '{org}' não encontrada ou sem acesso.");
            foreach (var repo in array.OfType<JsonObject>()) {
                if (repo["archived"]?.GetValue<bool>() == true || repo["fork"]?.GetValue<bool>() == true) continue;
                result.Add((repo["name"]!.GetValue<string>(), repo["default_branch"]?.GetValue<string>() ?? "main"));
            }

            if (array.Count < 100) break;
        }

        return result;
    }

    /// <summary>Conteúdo textual de um arquivo do branch padrão; nulo quando não existe.</summary>
    public async Task<string?> TryGetFileAsync(string org, string repo, string path, CancellationToken ct) {
        var node = await GetJsonAsync<JsonObject>($"https://api.github.com/repos/{org}/{repo}/contents/{path}", ct);
        if (node?["content"] is not { } content) return null;
        var base64 = content.GetValue<string>().Replace("\n", "", StringComparison.Ordinal);
        return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }

    /// <summary>Nomes dos arquivos de um diretório do branch padrão; vazio quando não existe.</summary>
    public async Task<IReadOnlyList<string>> ListDirectoryAsync(string org, string repo, string path, CancellationToken ct) {
        var node = await GetJsonAsync<JsonArray>($"https://api.github.com/repos/{org}/{repo}/contents/{path}", ct);
        return node?.OfType<JsonObject>().Where(e => e["type"]?.GetValue<string>() == "file")
            .Select(e => e["name"]!.GetValue<string>()).ToList() ?? [];
    }

    /// <summary>Regras efetivas (de todos os rulesets ativos) sobre um branch.</summary>
    public async Task<JsonArray> GetBranchRulesAsync(string org, string repo, string branch, CancellationToken ct) =>
        await GetJsonAsync<JsonArray>($"https://api.github.com/repos/{org}/{repo}/rules/branches/{Uri.EscapeDataString(branch)}", ct) ?? [];

    /// <summary>Nomes dos check runs concluídos com sucesso no commit mais recente do branch.</summary>
    public async Task<IReadOnlyList<string>> GetSuccessfulCheckNamesAsync(string org, string repo, string branch, CancellationToken ct) {
        var commit = await GetJsonAsync<JsonObject>($"https://api.github.com/repos/{org}/{repo}/commits/{Uri.EscapeDataString(branch)}", ct);
        if (commit?["sha"] is not { } sha) return [];
        var runs = await GetJsonAsync<JsonObject>($"https://api.github.com/repos/{org}/{repo}/commits/{sha.GetValue<string>()}/check-runs?per_page=100", ct);
        return runs?["check_runs"]?.AsArray().OfType<JsonObject>()
            .Where(r => r["conclusion"]?.GetValue<string>() == "success")
            .Select(r => r["name"]!.GetValue<string>()).Distinct(StringComparer.Ordinal).ToList() ?? [];
    }

    /// <summary>
    ///     Cria o ruleset do repositório ou atualiza o de mesmo nome. Um ruleset antigo de nome <paramref name="legacyName" />
    ///     é renomeado quando é o único, ou apagado quando o de nome novo já existe. Devolve "created" ou "updated".
    /// </summary>
    public async Task<string> UpsertRulesetAsync(string org, string repo, JsonObject ruleset, CancellationToken ct, string? legacyName = null) {
        var url = $"https://api.github.com/repos/{org}/{repo}/rulesets";
        if (legacyName is not null && await GetJsonAsync<JsonArray>($"{url}?includes_parents=false", ct) is { } existing) {
            long? Id(string name) => existing.OfType<JsonObject>().FirstOrDefault(r => r["name"]?.GetValue<string>() == name)?["id"]?.GetValue<long>();
            if (Id(legacyName) is { } legacy) {
                if (Id(ruleset["name"]!.GetValue<string>()) is null) {
                    await SendAsync(HttpMethod.Put, $"{url}/{legacy}", ruleset, ct);
                    return "updated";
                }

                using var deleted = await _http.DeleteAsync($"{url}/{legacy}", ct);
                if (!deleted.IsSuccessStatusCode)
                    throw new InvalidOperationException($"GitHub rejected deleting ruleset {legacyName} at {url} ({(int)deleted.StatusCode}): {await deleted.Content.ReadAsStringAsync(ct)}");
            }
        }

        return await UpsertRulesetAsync(url, ruleset, ct);
    }

    /// <summary>Cria o ruleset da organização ou atualiza o de mesmo nome. Devolve "created" ou "updated".</summary>
    public Task<string> UpsertOrganizationRulesetAsync(string org, JsonObject ruleset, CancellationToken ct) =>
        UpsertRulesetAsync($"https://api.github.com/orgs/{org}/rulesets", ruleset, ct);

    private async Task<string> UpsertRulesetAsync(string rulesetsUrl, JsonObject ruleset, CancellationToken ct) {
        var existing = await GetJsonAsync<JsonArray>(rulesetsUrl, ct) ?? [];
        var name = ruleset["name"]!.GetValue<string>();
        var match = existing.OfType<JsonObject>().FirstOrDefault(r => r["name"]?.GetValue<string>() == name);
        using var response = match is null
            ? await _http.PostAsJsonAsync(rulesetsUrl, ruleset, ct)
            : await _http.PutAsJsonAsync($"{rulesetsUrl}/{match["id"]!.GetValue<long>()}", ruleset, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub rejected the ruleset at {rulesetsUrl} ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(ct)}");
        return match is null ? "created" : "updated";
    }

    /// <summary>Nomes dos assets da última release; nulo quando o repositório não tem releases.</summary>
    public async Task<IReadOnlyList<string>?> GetLatestReleaseAssetNamesAsync(string org, string repo, CancellationToken ct) {
        var release = await GetJsonAsync<JsonObject>($"https://api.github.com/repos/{org}/{repo}/releases/latest", ct);
        return release?["assets"]?.AsArray().OfType<JsonObject>().Select(a => a["name"]!.GetValue<string>()).ToList();
    }

    /// <summary>Nível do selo OpenSSF Best Practices registrado para o repositório em bestpractices.dev; nulo quando não registrado.</summary>
    public async Task<string?> GetBestPracticesBadgeLevelAsync(string org, string repo, CancellationToken ct) {
        var projects = await GetJsonAsync<JsonArray>($"https://www.bestpractices.dev/projects.json?url=https://github.com/{org}/{repo}", ct, plainJson: true);
        return projects?.OfType<JsonObject>().FirstOrDefault()?["badge_level"]?.GetValue<string>();
    }

    /// <summary>
    ///     Sumário JSON (<c>&lt;artifact&gt;.json</c>) do artefato mais recente com esse nome produzido no
    ///     <paramref name="branch" /> ou no commit <paramref name="headSha" />; nulo quando não há artefato válido ou
    ///     o arquivo não está no zip.
    /// </summary>
    public async Task<JsonObject?> TryGetArtifactSummaryAsync(string org, string repo, string artifact, CancellationToken ct, string? branch = null, string? headSha = null) {
        var list = await GetJsonAsync<JsonObject>($"https://api.github.com/repos/{org}/{repo}/actions/artifacts?name={Uri.EscapeDataString(artifact)}&per_page=50", ct);
        var latest = list?["artifacts"]?.AsArray().OfType<JsonObject>()
            .Where(a => a["expired"]?.GetValue<bool>() != true)
            .Where(a => headSha is null ? a["workflow_run"]?["head_branch"]?.GetValue<string>() == branch : a["workflow_run"]?["head_sha"]?.GetValue<string>() == headSha)
            .OrderByDescending(a => a["created_at"]?.GetValue<string>(), StringComparer.Ordinal)
            .FirstOrDefault();
        if (latest?["archive_download_url"]?.GetValue<string>() is not { } url) return null;

        using var response = await _http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode) return null;
        using var zip = new System.IO.Compression.ZipArchive(await response.Content.ReadAsStreamAsync(ct));
        var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals($"{artifact}.json", StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;
        using var reader = new StreamReader(entry.Open());
        return JsonNode.Parse(await reader.ReadToEndAsync(ct)) as JsonObject;
    }

    /// <summary>PR pelo número; nula quando não existe.</summary>
    public Task<JsonObject?> GetPullRequestAsync(string org, string repo, int number, CancellationToken ct) =>
        GetJsonAsync<JsonObject>($"https://api.github.com/repos/{org}/{repo}/pulls/{number}", ct);

    /// <summary>PR aberta cujo head é <paramref name="sha" />; nula quando não há.</summary>
    public async Task<JsonObject?> FindOpenPullRequestBySha(string org, string repo, string sha, CancellationToken ct) {
        var pulls = await GetJsonAsync<JsonArray>($"https://api.github.com/repos/{org}/{repo}/commits/{sha}/pulls", ct);
        var open = pulls?.OfType<JsonObject>().FirstOrDefault(p => p["state"]?.GetValue<string>() == "open" && p["head"]?["sha"]?.GetValue<string>() == sha);
        return open is null ? null : await GetPullRequestAsync(org, repo, open["number"]!.GetValue<int>(), ct);
    }

    /// <summary>Caminhos dos arquivos tocados pela PR.</summary>
    public async Task<IReadOnlyList<string>> ListPullRequestFilesAsync(string org, string repo, int number, CancellationToken ct) {
        var files = new List<string>();
        for (var page = 1; ; page++) {
            var array = await GetJsonAsync<JsonArray>($"https://api.github.com/repos/{org}/{repo}/pulls/{number}/files?per_page=100&page={page}", ct) ?? [];
            files.AddRange(array.OfType<JsonObject>().Select(f => f["filename"]!.GetValue<string>()));
            if (array.Count < 100) return files;
        }
    }

    /// <summary>Mensagens dos commits da PR.</summary>
    public async Task<IReadOnlyList<string>> ListPullRequestCommitMessagesAsync(string org, string repo, int number, CancellationToken ct) {
        var array = await GetJsonAsync<JsonArray>($"https://api.github.com/repos/{org}/{repo}/pulls/{number}/commits?per_page=100", ct) ?? [];
        return array.OfType<JsonObject>().Select(c => c["commit"]?["message"]?.GetValue<string>() ?? "").ToList();
    }

    /// <summary>Logins dos membros do time; nulo quando o time não existe ou o token não pode ler membros da organização.</summary>
    public async Task<IReadOnlyList<string>?> ListTeamMembersAsync(string org, string team, CancellationToken ct) =>
        (await GetJsonAsync<JsonArray>($"https://api.github.com/orgs/{org}/teams/{team}/members?per_page=100", ct, nullOnForbidden: true))
        ?.OfType<JsonObject>().Select(m => m["login"]!.GetValue<string>()).ToList();

    /// <summary>Revisões da PR, na ordem em que foram enviadas.</summary>
    public async Task<JsonArray> ListPullRequestReviewsAsync(string org, string repo, int number, CancellationToken ct) =>
        await GetJsonAsync<JsonArray>($"https://api.github.com/repos/{org}/{repo}/pulls/{number}/reviews?per_page=100", ct) ?? [];

    /// <summary>Nome do check → conclusão no commit (nula enquanto não concluiu); fica a execução mais recente de cada nome.</summary>
    public async Task<IReadOnlyDictionary<string, string?>> GetCheckConclusionsAsync(string org, string repo, string sha, CancellationToken ct) {
        var runs = await GetJsonAsync<JsonObject>($"https://api.github.com/repos/{org}/{repo}/commits/{sha}/check-runs?per_page=100", ct);
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        var ordered = (runs?["check_runs"]?.AsArray().OfType<JsonObject>() ?? []).OrderBy(r => r["started_at"]?.GetValue<string>(), StringComparer.Ordinal);
        foreach (var run in ordered)
            result[run["name"]!.GetValue<string>()] = run["status"]?.GetValue<string>() == "completed" ? run["conclusion"]?.GetValue<string>() : null;
        return result;
    }

    /// <summary>Corpos dos comentários da PR/issue.</summary>
    public async Task<IReadOnlyList<string>> ListIssueCommentsAsync(string org, string repo, int number, CancellationToken ct) {
        var array = await GetJsonAsync<JsonArray>($"https://api.github.com/repos/{org}/{repo}/issues/{number}/comments?per_page=100", ct) ?? [];
        return array.OfType<JsonObject>().Select(c => c["body"]?.GetValue<string>() ?? "").ToList();
    }

    /// <summary>Comenta na PR/issue.</summary>
    public Task CommentAsync(string org, string repo, int number, string body, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"https://api.github.com/repos/{org}/{repo}/issues/{number}/comments", new JsonObject { ["body"] = body }, ct);

    /// <summary>Aprova a PR com o token atual (precisa ser uma identidade diferente do autor, ex.: a App).</summary>
    public Task ApprovePullRequestAsync(string org, string repo, int number, string body, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"https://api.github.com/repos/{org}/{repo}/pulls/{number}/reviews", new JsonObject { ["event"] = "APPROVE", ["body"] = body }, ct);

    /// <summary>Habilita o auto-merge da PR (GraphQL); o repositório precisa permitir auto-merge.</summary>
    public async Task EnableAutoMergeAsync(string pullRequestNodeId, string mergeMethod, CancellationToken ct) {
        var query = new JsonObject {
            ["query"] = "mutation($id: ID!, $method: PullRequestMergeMethod!) { enablePullRequestAutoMerge(input: {pullRequestId: $id, mergeMethod: $method}) { clientMutationId } }",
            ["variables"] = new JsonObject { ["id"] = pullRequestNodeId, ["method"] = mergeMethod.ToUpperInvariant() }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.github.com/graphql") { Content = JsonContent.Create(query) };
        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode || (JsonNode.Parse(body) as JsonObject)?["errors"] is JsonArray { Count: > 0 } errors)
            throw new InvalidOperationException($"GitHub rejected enablePullRequestAutoMerge ({(int)response.StatusCode}): {body[..Math.Min(body.Length, 300)]}");
    }

    /// <summary>Altera configurações do repositório (<c>PATCH /repos/{org}/{repo}</c>), ex.: auto-merge.</summary>
    public Task PatchRepositoryAsync(string org, string repo, JsonObject settings, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"https://api.github.com/repos/{org}/{repo}", settings, ct);

    /// <summary>Troca o JWT da GitHub App por um token da instalação na organização.</summary>
    public async Task<string> CreateInstallationTokenAsync(string org, string appJwt, CancellationToken ct) {
        using var find = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/orgs/{org}/installation");
        find.Headers.Authorization = new AuthenticationHeaderValue("Bearer", appJwt);
        using var found = await _http.SendAsync(find, ct);
        if (!found.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub App is not installed in '{org}' ({(int)found.StatusCode}): {await found.Content.ReadAsStringAsync(ct)}");
        var installationId = (JsonNode.Parse(await found.Content.ReadAsStringAsync(ct)) as JsonObject)?["id"]?.GetValue<long>();

        using var create = new HttpRequestMessage(HttpMethod.Post, $"https://api.github.com/app/installations/{installationId}/access_tokens");
        create.Headers.Authorization = new AuthenticationHeaderValue("Bearer", appJwt);
        using var created = await _http.SendAsync(create, ct);
        if (!created.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub rejected the installation token ({(int)created.StatusCode}): {await created.Content.ReadAsStringAsync(ct)}");
        return (JsonNode.Parse(await created.Content.ReadAsStringAsync(ct)) as JsonObject)?["token"]?.GetValue<string>()
               ?? throw new InvalidOperationException("installation token missing in the response");
    }

    /// <summary>Slug da própria GitHub App (<c>GET /app</c> com o JWT): <c>&lt;slug&gt;[bot]</c> é o login que assina as aprovações. Nulo quando não legível.</summary>
    public async Task<string?> GetAppSlugAsync(string appJwt, CancellationToken ct) {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/app");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", appJwt);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return null;
        return (JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) as JsonObject)?["slug"]?.GetValue<string>();
    }

    /// <summary>Objeto da organização (<c>GET /orgs/{org}</c>); vazio quando não há acesso.</summary>
    public async Task<JsonObject> GetOrganizationAsync(string org, CancellationToken ct) =>
        await GetJsonAsync<JsonObject>($"https://api.github.com/orgs/{org}", ct) ?? new JsonObject();

    /// <summary>Permissões padrão de workflow do Actions na organização.</summary>
    public async Task<JsonObject> GetWorkflowPermissionsAsync(string org, CancellationToken ct) =>
        await GetJsonAsync<JsonObject>($"https://api.github.com/orgs/{org}/actions/permissions/workflow", ct) ?? new JsonObject();

    /// <summary>Rulesets da organização; nulo quando o plano do GitHub não oferece rulesets de organização (403 no plano Free).</summary>
    public Task<JsonArray?> ListOrganizationRulesetsAsync(string org, CancellationToken ct) =>
        GetJsonAsync<JsonArray>($"https://api.github.com/orgs/{org}/rulesets", ct, nullOnForbidden: true);

    /// <summary>Altera configurações da organização (<c>PATCH /orgs/{org}</c>).</summary>
    public Task PatchOrganizationAsync(string org, JsonObject settings, CancellationToken ct) =>
        SendAsync(HttpMethod.Patch, $"https://api.github.com/orgs/{org}", settings, ct);

    /// <summary>Define as permissões padrão de workflow do Actions na organização.</summary>
    public Task PutWorkflowPermissionsAsync(string org, JsonObject permissions, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"https://api.github.com/orgs/{org}/actions/permissions/workflow", permissions, ct);

    private async Task SendAsync(HttpMethod method, string url, JsonObject body, CancellationToken ct) {
        using var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body) };
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub rejected {method} {url} ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(ct)}");
    }

    /// <summary>Resultado publicado do OpenSSF Scorecard; nulo quando o projeto não publica.</summary>
    public async Task<ScorecardResult?> GetScorecardAsync(string org, string repo, CancellationToken ct) {
        var node = await GetJsonAsync<JsonObject>($"https://api.scorecard.dev/projects/github.com/{org}/{repo}", ct, plainJson: true);
        if (node?["score"] is not { } score) return null;
        var checks = node["checks"]?.AsArray().OfType<JsonObject>()
            .ToDictionary(c => c["name"]!.GetValue<string>(), c => c["score"]?.GetValue<double>() ?? -1, StringComparer.OrdinalIgnoreCase)
                     ?? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        return new ScorecardResult(score.GetValue<double>(), checks);
    }

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken ct, bool plainJson = false, bool nullOnForbidden = false) where T : JsonNode {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (plainJson) {
            // A API do Scorecard só aceita application/json e não deve receber o token do GitHub.
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Authorization = null;
        }

        using var response = await _http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.NotFound || (nullOnForbidden && response.StatusCode == HttpStatusCode.Forbidden)) return null;
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"GitHub respondeu {(int)response.StatusCode} em {url}: {await response.Content.ReadAsStringAsync(ct)}");
        var node = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        return node as T;
    }
}

/// <summary>Nota agregada e por check do OpenSSF Scorecard.</summary>
public sealed record ScorecardResult(double Score, IReadOnlyDictionary<string, double> Checks);
