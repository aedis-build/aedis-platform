using System.Text.Json.Nodes;

namespace Aedis.Cli.Org;

/// <summary>
///     Monta os guardrails que materializam o nível pedido. Ruleset do branch padrão: L1 proíbe force-push e
///     exclusão e exige PR; L2 soma histórico linear, resolução de threads, dismiss de revisões obsoletas e checks
///     obrigatórios; L3 soma revisão de code owner e aprovação do último push. O mesmo ruleset vale no nível da
///     organização (todos os repositórios, sem checks obrigatórios, que são por repositório). Configurações de
///     organização e do Actions acompanham os critérios <c>ORG-xx</c> de best-practices.yaml. O papel de
///     administrador pode contornar via PR (fica auditável) para organizações com um só mantenedor.
/// </summary>
public static class RulesetGuardrail
{
    /// <summary>Nome do ruleset gerido pela CLI (criado ou atualizado pelo nome), no repositório e na organização.</summary>
    public const string Name = "aedis-main";

    /// <summary>Corpo do ruleset para a API do GitHub no nível pedido; com <paramref name="allRepositories" />, vira ruleset de organização.</summary>
    public static JsonObject Build(int level, int approvals, IReadOnlyList<string> requiredChecks, bool adminBypass, bool allRepositories = false) {
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

        var conditions = new JsonObject {
            ["ref_name"] = new JsonObject { ["include"] = new JsonArray("~DEFAULT_BRANCH", "refs/heads/develop"), ["exclude"] = new JsonArray() }
        };
        if (allRepositories)
            conditions["repository_name"] = new JsonObject { ["include"] = new JsonArray("~ALL"), ["exclude"] = new JsonArray() };

        return new JsonObject {
            ["name"] = Name,
            ["target"] = "branch",
            ["enforcement"] = "active",
            ["conditions"] = conditions,
            ["rules"] = rules,
            ["bypass_actors"] = bypass
        };
    }

    /// <summary>Configurações de organização (<c>PATCH /orgs/{org}</c>) que os critérios ORG-xx exigem até o nível; 2FA não tem API de escrita.</summary>
    public static JsonObject OrganizationSettings(int level) {
        var settings = new JsonObject {
            ["dependabot_alerts_enabled_for_new_repositories"] = true
        };
        if (level >= 2) settings["dependabot_security_updates_enabled_for_new_repositories"] = true;
        if (level >= 3) {
            settings["secret_scanning_enabled_for_new_repositories"] = true;
            settings["secret_scanning_push_protection_enabled_for_new_repositories"] = true;
        }

        return settings;
    }

    /// <summary>Permissões de workflow do Actions (<c>PUT /orgs/{org}/actions/permissions/workflow</c>): token só leitura e sem aprovar PRs.</summary>
    public static JsonObject WorkflowPermissions() => new() {
        ["default_workflow_permissions"] = "read",
        ["can_approve_pull_request_reviews"] = false
    };
}
