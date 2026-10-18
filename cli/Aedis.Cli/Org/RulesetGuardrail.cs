using System.Text.Json.Nodes;

namespace Aedis.Cli.Org;

/// <summary>
///     Monta o ruleset do branch padrão que materializa o nível pedido: L1 proíbe force-push e exclusão e
///     exige PR; L2 soma histórico linear, resolução de threads, dismiss de revisões obsoletas e checks
///     obrigatórios; L3 soma revisão de code owner e aprovação do último push. O papel de administrador
///     pode contornar via PR (fica auditável) para organizações com um só mantenedor.
/// </summary>
public static class RulesetGuardrail
{
    /// <summary>Nome do ruleset gerido pela CLI (criado ou atualizado pelo nome).</summary>
    public const string Name = "aedis-main";

    /// <summary>Corpo do ruleset para a API do GitHub no nível pedido.</summary>
    public static JsonObject Build(int level, int approvals, IReadOnlyList<string> requiredChecks, bool adminBypass) {
        var rules = new JsonArray {
            new JsonObject { ["type"] = "deletion" },
            new JsonObject { ["type"] = "non_fast_forward" }
        };
        if (level >= 2) rules.Add(new JsonObject { ["type"] = "required_linear_history" });

        rules.Add(new JsonObject {
            ["type"] = "pull_request",
            ["parameters"] = new JsonObject {
                ["required_approving_review_count"] = approvals,
                ["dismiss_stale_reviews_on_push"] = level >= 2,
                ["require_code_owner_review"] = level >= 3,
                ["require_last_push_approval"] = level >= 3,
                ["required_review_thread_resolution"] = level >= 2,
                ["allowed_merge_methods"] = new JsonArray("squash")
            }
        });

        if (level >= 2 && requiredChecks.Count > 0)
            rules.Add(new JsonObject {
                ["type"] = "required_status_checks",
                ["parameters"] = new JsonObject {
                    ["strict_required_status_checks_policy"] = true,
                    ["do_not_enforce_on_create"] = true,
                    ["required_status_checks"] = new JsonArray(requiredChecks.Select(c => (JsonNode)new JsonObject { ["context"] = c }).ToArray())
                }
            });

        var bypass = new JsonArray();
        if (adminBypass)
            bypass.Add(new JsonObject { ["actor_id"] = 5, ["actor_type"] = "RepositoryRole", ["bypass_mode"] = "pull_request" });

        return new JsonObject {
            ["name"] = Name,
            ["target"] = "branch",
            ["enforcement"] = "active",
            ["conditions"] = new JsonObject {
                ["ref_name"] = new JsonObject { ["include"] = new JsonArray("~DEFAULT_BRANCH"), ["exclude"] = new JsonArray() }
            },
            ["rules"] = rules,
            ["bypass_actors"] = bypass
        };
    }

    /// <summary>
    ///     Checks que viram obrigatórios quando o chamador não os informa: os que passaram no último commit do
    ///     branch padrão, menos os informativos (Scorecard, espelhos de imagem).
    /// </summary>
    public static IReadOnlyList<string> DefaultRequiredChecks(IEnumerable<string> successfulChecks) =>
        successfulChecks
            .Where(c => !c.Contains("Scorecard", StringComparison.OrdinalIgnoreCase) && !c.Contains("mirror", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();
}
