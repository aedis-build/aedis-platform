using System.CommandLine;
using System.Text.Json.Nodes;
using Aedis.Cli.Org;

namespace Aedis.Cli.Commands;

/// <summary>
///     <c>aedis pr gate</c>: avalia uma PR contra a política de merge do repositório (lida do branch padrão) e,
///     quando tudo passa e há credenciais de GitHub App, aprova a PR como a App e habilita o auto-merge. O
///     GITHUB_TOKEN do Actions não aprova (a organização desliga isso de propósito); sem App, o comando só
///     avalia, grava <c>merge-gate-summary.json</c> e comenta na PR. Nunca lê o código da PR: só metadados,
///     checks e sumários pela API.
/// </summary>
public static class PrGateCommand
{
    /// <summary>Monta o comando; <paramref name="handlerFactory" /> permite simular a API nos testes.</summary>
    public static Command Create(Func<HttpMessageHandler>? handlerFactory = null) {
        var org = new Option<string>("--org") { Description = "GitHub organization or owner", Required = true };
        var repo = new Option<string>("--repo") { Description = "Repository name", Required = true };
        var pr = new Option<int?>("--pr") { Description = "Pull request number" };
        var headSha = new Option<string?>("--head-sha") { Description = "Head commit; resolves the open pull request that has it (used from workflow_run)" };
        var policy = new Option<string>("--policy") { Description = "Policy path in the default branch", DefaultValueFactory = _ => ".github/aedis-merge-policy.yaml" };
        var approve = new Option<bool>("--approve") { Description = "Approve as the GitHub App and enable auto-merge when the decision is approve", DefaultValueFactory = _ => true };
        var appId = new Option<string?>("--app-id") { Description = "GitHub App id (default: AEDIS_APP_ID)" };
        var appKey = new Option<string?>("--app-key") { Description = "GitHub App private key, PEM text or file path (default: AEDIS_APP_PRIVATE_KEY)" };
        var output = new Option<string>("--out") { Description = "Summary directory", DefaultValueFactory = _ => "." };
        var token = new Option<string?>("--token") { Description = "Read token (default: GITHUB_TOKEN, GH_TOKEN or `gh auth token`)" };

        var command = new Command("gate", "Evaluates a pull request against .github/aedis-merge-policy.yaml and approves it as the Aedis Compliance GitHub App when every criterion passes.") {
            org, repo, pr, headSha, policy, approve, appId, appKey, output, token
        };
        command.SetAction(async (parse, cancellationToken) => {
            var handler = handlerFactory?.Invoke() ?? new HttpClientHandler();
            var api = new GitHubApi(new HttpClient(handler), parse.GetValue(token) ?? GitHubApi.ResolveToken());
            var (orgName, repoName) = (parse.GetValue(org)!, parse.GetValue(repo)!);
            var outDir = Path.GetFullPath(parse.GetValue(output)!);
            Directory.CreateDirectory(outDir);

            var pull = parse.GetValue(pr) is { } number
                ? await api.GetPullRequestAsync(orgName, repoName, number, cancellationToken)
                : await api.FindOpenPullRequestBySha(orgName, repoName, parse.GetValue(headSha) ?? throw new InvalidOperationException("pass --pr or --head-sha"), cancellationToken);
            if (pull is null) {
                Console.WriteLine("no open pull request for the given number/head; nothing to do");
                return 0;
            }

            var mergePolicy = MergePolicy.Parse(await api.TryGetFileAsync(orgName, repoName, parse.GetValue(policy)!, cancellationToken));
            var snapshot = await SnapshotAsync(api, orgName, repoName, pull, mergePolicy, cancellationToken);
            var result = MergeGate.Evaluate(mergePolicy, snapshot);
            string? action = null;

            // A App é quem aprova e quem comenta: o GITHUB_TOKEN do job é só leitura e a organização proíbe que ele aprove.
            GitHubApi? app = null;
            var id = parse.GetValue(appId) ?? Environment.GetEnvironmentVariable("AEDIS_APP_ID");
            var key = parse.GetValue(appKey) ?? Environment.GetEnvironmentVariable("AEDIS_APP_PRIVATE_KEY");
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(key)) {
                if (File.Exists(key)) key = await File.ReadAllTextAsync(key, cancellationToken);
                var installationToken = await api.CreateInstallationTokenAsync(orgName, GitHubAppAuth.CreateJwt(id, key), cancellationToken);
                app = new GitHubApi(new HttpClient(handlerFactory?.Invoke() ?? new HttpClientHandler()), installationToken);
            }

            if (result.Decision == "approve" && parse.GetValue(approve)) {
                if (app is null) {
                    action = "Not approved: the GitHub App credentials (AEDIS_APP_ID/AEDIS_APP_PRIVATE_KEY) are missing in this repository.";
                }
                else {
                    await app.ApprovePullRequestAsync(orgName, repoName, snapshot.Number, $"✅ Every criterion of the merge policy passed for `{snapshot.HeadSha[..7]}` (Aedis Compliance).", cancellationToken);
                    try {
                        await app.EnableAutoMergeAsync(snapshot.NodeId, mergePolicy.MergeMethod, cancellationToken);
                        action = "Auto-merge is on: GitHub merges this pull request as soon as every required check passes.";
                    }
                    catch (InvalidOperationException ex) {
                        Console.Error.WriteLine($"auto-merge not enabled: {ex.Message}");
                        action = MergeGate.AutoMergeNote(ex.Message);
                    }
                }
            }

            await File.WriteAllTextAsync(Path.Combine(outDir, "merge-gate-summary.json"), MergeGate.ToJson(orgName, repoName, snapshot, result, action), cancellationToken);
            Console.WriteLine($"PR #{snapshot.Number} @ {snapshot.HeadSha[..7]}: {result.Decision}{(action is null ? "" : $" — {action}")}");
            foreach (var c in result.Checks.Where(c => c.Status != "pass"))
                Console.WriteLine($"        [{c.Status}] {c.Id}{(string.IsNullOrEmpty(c.Detail) ? "" : " — " + c.Detail)}");

            if (result.Decision != "wait") {
                var marker = $"{MergeGate.CommentMarker} `{snapshot.HeadSha[..7]}`";
                var comments = await api.ListIssueCommentsAsync(orgName, repoName, snapshot.Number, cancellationToken);
                if (!comments.Any(c => c.Contains(marker, StringComparison.Ordinal)))
                    try {
                        await (app ?? api).CommentAsync(orgName, repoName, snapshot.Number, MergeGate.ToComment(snapshot, result, action), cancellationToken);
                    }
                    catch (InvalidOperationException ex) {
                        Console.Error.WriteLine($"comment skipped: {ex.Message}");
                    }
            }

            return 0;
        });
        return command;
    }

    /// <summary>Baixa arquivos, commits, checks, sumários do head e a cobertura do branch base.</summary>
    public static async Task<PullRequestSnapshot> SnapshotAsync(GitHubApi api, string org, string repo, JsonObject pull, MergePolicy policy, CancellationToken ct) {
        var number = pull["number"]!.GetValue<int>();
        var head = pull["head"]!["sha"]!.GetValue<string>();
        var baseBranch = pull["base"]!["ref"]!.GetValue<string>();
        var author = pull["user"]?["login"]?.GetValue<string>() ?? "";
        var rules = await api.GetBranchRulesAsync(org, repo, baseBranch, ct);
        var required = rules.OfType<JsonObject>().Where(r => r["type"]?.GetValue<string>() == "required_status_checks")
            .SelectMany(r => (r["parameters"]?["required_status_checks"] as JsonArray)?.OfType<JsonObject>() ?? [])
            .Select(c => c["context"]!.GetValue<string>()).Distinct(StringComparer.Ordinal).ToList();

        var summaries = new Dictionary<string, JsonObject?>(StringComparer.Ordinal);
        foreach (var artifact in policy.RequiredSummaries)
            summaries[artifact] = await api.TryGetArtifactSummaryAsync(org, repo, artifact, ct, headSha: head);
        if (policy.CoverageNoRegression && !summaries.ContainsKey("coverage-summary"))
            summaries["coverage-summary"] = await api.TryGetArtifactSummaryAsync(org, repo, "coverage-summary", ct, headSha: head);

        return new PullRequestSnapshot {
            Number = number,
            NodeId = pull["node_id"]?.GetValue<string>() ?? "",
            Author = author,
            HeadSha = head,
            BaseBranch = baseBranch,
            Title = pull["title"]?.GetValue<string>() ?? "",
            Body = pull["body"]?.GetValue<string>() ?? "",
            IsDraft = pull["draft"]?.GetValue<bool>() ?? false,
            FromFork = pull["head"]?["repo"]?["fork"]?.GetValue<bool>() ?? false
                       || (pull["head"]?["repo"]?["full_name"]?.GetValue<string>() is { } full && !full.Equals($"{org}/{repo}", StringComparison.OrdinalIgnoreCase)),
            MergeableState = pull["mergeable_state"]?.GetValue<string>() ?? "unknown",
            ChangedLines = (pull["additions"]?.GetValue<int>() ?? 0) + (pull["deletions"]?.GetValue<int>() ?? 0),
            Files = await api.ListPullRequestFilesAsync(org, repo, number, ct),
            CommitMessages = await api.ListPullRequestCommitMessagesAsync(org, repo, number, ct),
            RequiredChecks = required,
            CheckConclusions = await api.GetCheckConclusionsAsync(org, repo, head, ct),
            Summaries = summaries,
            BaseCoverage = policy.CoverageNoRegression ? await api.TryGetArtifactSummaryAsync(org, repo, "coverage-summary", ct, branch: baseBranch) : null,
            HumanApprovers = MergeGate.HumanApprovers(await api.ListPullRequestReviewsAsync(org, repo, number, ct), author, head)
        };
    }
}
