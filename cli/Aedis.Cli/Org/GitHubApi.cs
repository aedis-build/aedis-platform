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

    /// <summary>Cria o ruleset do repositório ou atualiza o de mesmo nome. Devolve "created" ou "updated".</summary>
    public Task<string> UpsertRulesetAsync(string org, string repo, JsonObject ruleset, CancellationToken ct) =>
        UpsertRulesetAsync($"https://api.github.com/repos/{org}/{repo}/rulesets", ruleset, ct);

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
