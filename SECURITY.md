# Política de segurança

## Versões suportadas

| Versão do chart `aedis-app` | Suporte |
|---|---|
| 0.x (preview) | correções na linha `main` |

## Como reportar uma vulnerabilidade

Use **Report a vulnerability** na aba Security deste repositório (GitHub Private Vulnerability Reporting).
Não abra issue pública para vulnerabilidades.

Compromissos:

- confirmação de recebimento em até 3 dias úteis;
- avaliação inicial de severidade (CVSS) em até 7 dias;
- correção ou mitigação documentada para severidade alta/crítica em até 30 dias;
- crédito ao pesquisador no advisory, se desejar.

## Escopo

- Chart `charts/aedis-app` e `charts/aedis-cluster` (manifestos gerados, defaults de segurança).
- Políticas `policies/`, ruleset `semgrep/`, workflows reutilizáveis `.github/workflows/`.
- CLI `cli/` e scripts `scripts/`.

Vulnerabilidades no framework Aedis (`aedis-build/aedis`) seguem a política daquele repositório.

## Práticas deste repositório

- Actions pinadas por SHA; `permissions: {}` por padrão; sem credenciais de longa duração (OIDC).
- Dependências atualizadas por Dependabot; OpenSSF Scorecard publicado; selo OpenSSF Best Practices em andamento.
- Chart assinado com cosign na publicação (OCI).
