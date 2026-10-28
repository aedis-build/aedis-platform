#!/usr/bin/env python3
"""Compatibilidade (removido em v2): o sumário de dependências vive em aedis-build/aedis-sca (deps-summary.py) desde a
v1.1.0, mas o coverage.yml e o coverage-js.yml pinados antes dela (≤ v1.0.3) ainda executam este caminho no checkout
da plataforma, e esse checkout cai em `main` quando `github.job_workflow_sha` vem vazio. Este shim busca o script da
action, pinado, e o executa com os mesmos argumentos (--input, --accept, --out, --self-test)."""
import os, runpy, sys, urllib.request

PIN = "516872ee6ede29d8dc92d932175700c1958693c3"  # aedis-sca, repin to v1.0.0 once released
URL = f"https://raw.githubusercontent.com/aedis-build/aedis-sca/{PIN}/deps-summary.py"
path = os.path.join(os.environ.get("RUNNER_TEMP") or os.environ.get("TMPDIR") or "/tmp", f"aedis-sca-{PIN}-deps-summary.py")
if not os.path.exists(path):
    try:
        urllib.request.urlretrieve(URL, path)
    except OSError as e:
        sys.exit(f"::error::scripts/deps-summary.py moved to aedis-build/aedis-sca and could not be fetched ({e}); pin a platform release >= v1.1.0")
runpy.run_path(path, run_name="__main__")
