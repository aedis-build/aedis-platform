# Níveis de conformidade

Os níveis descrevem **controles implementados e verificáveis** por `aedis platform validate --level N` e pelos
workflows. Não são certificação. A régua base é externa e auditável: OpenSSF Scorecard, selo OpenSSF Best
Practices e cobertura de código; os controles de plataforma (`PLT-xx`) somam a ela.

| Nível | Régua base | Controles de plataforma |
|---|---|---|
| **L1 Baseline** | Scorecard roda e publica (sem 0 em Token-Permissions, Dangerous-Workflow, Binary-Artifacts); checklist Best Practices *passing*; cobertura publicada, ≥ 60 % linhas | baseline de pod do chart, schema, segredos via ESO, probes, PDB |
| **L2 Hardened** | Scorecard ≥ 7 e Branch-Protection, Code-Review, Pinned-Dependencies, SAST, Vulnerabilities ≥ 8; Best Practices *silver*; ≥ 75 % linhas, ≥ 60 % branches, sem regressão | imagem assinada (cosign) e pinada por digest em prod, SBOM anexado, Kyverno em enforce, NetworkPolicy de egresso, PSA restricted, gate SAST |
| **L3 Regulated** | Scorecard ≥ 9 com todo check sob controle do processo em 10 (Dangerous-Workflow, Token-Permissions, Binary-Artifacts, Vulnerabilities, Dependency-Update-Tool, Security-Policy, License, SAST, Pinned-Dependencies, Code-Review, Signed-Releases, Fuzzing) e Branch-Protection ≥ 8; Best Practices *gold*; ≥ 85 % linhas, ≥ 70 % branches | gate DAST, attestation verificada no admission, prod só por PR com aprovação, drift report, WAF declarado, evidências retidas |

Os limiares vivem em `conformance/levels.yaml`; um tenant pode subi-los, nunca baixá-los.

A régua é a mesma para qualquer linguagem. Em .NET a cobertura vem do `coverage.yml` (coverlet) e em
JavaScript/TypeScript do `coverage-js.yml` (pnpm + reporter Cobertura do vitest/jest); os dois aplicam o mesmo
`scripts/coverage-gate.py` e publicam o mesmo `coverage-summary.json`. O `sast.yml` traz regras Aedis para C# e
TypeScript/React (repositórios JS acrescentam `extra-configs: p/javascript p/typescript p/react`). Para o check
*Fuzzing* do Scorecard, testes baseados em propriedades contam: FsCheck em C#/F# e fast-check em
JavaScript/TypeScript, além de ClusterFuzzLite e OSS-Fuzz.

## Controles de plataforma (resumo)

| ID | Controle | Onde | Mapeamentos |
|---|---|---|---|
| PLT-01 | Pod non-root, read-only, sem capabilities, seccompar | chart `_helpers.tpl` | CIS K8s 5.2, OWASP K8s K01/K09, NIST PR.PT |
| PLT-02 | Segredos fora do git, sincronizados do cofre | `externalsecret.yaml` | OWASP Top 10 A02, ISO A.8.24, NIST PR.DS |
| PLT-03 | Identidade de workload sem chave estática | `serviceaccount.yaml` | OWASP K8s K03, NIST PR.AC |
| PLT-04 | Egresso restrito (anti-SSRF) | `networkpolicy.yaml` | OWASP A10 (SSRF), OWASP K8s K07 |
| PLT-05 | Validação de manifestos (schema, kubeconform, Trivy config) | `validate.yml` | NIST PR.IP |
| PLT-06 | Imagem assinada e verificada | `build-image.yml`, Kyverno | SLSA, NIST SSDF PS.2 |
| PLT-07 | SBOM e proveniência | `build-image.yml` | NIST SSDF PS.3, EO 14028 |
| PLT-08 | Pin por digest em prod | schema + `promote.yml` | SLSA |
| PLT-09 | Admission policy (Kyverno) e PSA restricted | `policies/kyverno` | CIS K8s 5.2, OWASP K8s K01 |
| PLT-10 | Promoção para prod por PR com revisão | CODEOWNERS + rulesets | ISO A.8.32, NIST PR.IP-3 |
| PLT-11 | Canário com análise automática | `rollout.yaml` | NIST DE.CM |
| PLT-12 | Proxies e hosts confiáveis fixados no app | `_env.tpl` | OWASP A05 |
| PLT-13 | SAST no código (Semgrep + regras Aedis) | `sast.yml` | OWASP A03, NIST SSDF PW.7 |
| PLT-14 | DAST na superfície exposta | `.github/actions/dast` | OWASP Top 10, NIST SSDF PW.8 |
| PLT-15 | Varredura contínua das imagens em uso | `image-scan.yml` | NIST SSDF PW.4, RV.1 |
| PLT-16 | Detecção de drift em prod | `drift-report.yml` | NIST DE.CM |

Mapeamentos completos (OWASP Top 10 2021, OWASP Kubernetes Top 10, NIST CSF 2.0, NIST SSDF, ISO 27001:2022 A.8,
CIS Kubernetes Benchmark) em `docs/compliance/platform-controls.md` (Fase 5).
