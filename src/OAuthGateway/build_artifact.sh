#!/usr/bin/env bash
# Builds the secret-free deployment zip for Azure App Service Linux (Python 3.12, code deployment).
# Explicit allowlist: only gateway/*.py, requirements.txt and Linux wheels. No tests, no .env, no state.
set -euo pipefail
OUT="${1:-dist/gateway.zip}"; VERSION="${2:-local}"
STAGE="$(mktemp -d)"
mkdir -p "$STAGE/gateway" "$STAGE/.python_packages/lib/site-packages"
cp gateway/__init__.py gateway/app.py gateway/config.py gateway/server.py "$STAGE/gateway/"
cp requirements.txt "$STAGE/"
echo "$VERSION" > "$STAGE/VERSION"
grep -v 'sys_platform == "win32"' requirements.txt > "$STAGE/req.linux.txt"
"${PYTHON:-python}" -m pip install --quiet --no-deps --only-binary=:all: --platform manylinux2014_x86_64 --platform manylinux_2_17_x86_64 \
  --platform manylinux_2_28_x86_64 --platform any --python-version 3.12 --implementation cp \
  --target "$STAGE/.python_packages/lib/site-packages" -r "$STAGE/req.linux.txt"
rm -f "$STAGE/req.linux.txt"
# smaller artifact: bytecode is regenerated at runtime and only inflates the zip (F1 quota / fewer files)
find "$STAGE" -type d -name __pycache__ -prune -exec rm -rf {} +
find "$STAGE" -name '*.pyc' -delete
# secret scan of the artifact (fail closed)
if grep -rIlE "(client_secret|BEGIN (RSA |EC )?PRIVATE KEY)\s*[:=]\s*['\"][^'\"]{8,}" "$STAGE/gateway" "$STAGE/VERSION"; then
  echo "secret-like content found in artifact" >&2; exit 1
fi
mkdir -p "$(dirname "$OUT")"; OUT_ABS="$(cd "$(dirname "$OUT")" && pwd)/$(basename "$OUT")"
(cd "$STAGE" && "${PYTHON:-python}" -c "import shutil,sys;shutil.make_archive(sys.argv[1][:-4],'zip','.')" "$OUT_ABS")
echo "built $OUT ($VERSION)"
