#!/usr/bin/env bash
# New releases are drafts until assets are uploaded. Retries reuse published bytes.
set -euo pipefail
version="$(python3 scripts/distribution.py version)"
tag="v${version}"
archive="ASS-Fonts_${version}.zip"
mkdir -p artifacts/published

if ! gh release view "$tag" --repo "$GITHUB_REPOSITORY" \
    --json isDraft,targetCommitish,publishedAt,body > artifacts/release.json; then
  gh release create "$tag" --repo "$GITHUB_REPOSITORY" \
    --target "$GITHUB_SHA" --draft --title "ASS Fonts ${version}" --generate-notes
  gh release view "$tag" --repo "$GITHUB_REPOSITORY" \
    --json isDraft,targetCommitish,publishedAt,body > artifacts/release.json
fi

draft="$(python3 -c 'import json; print(str(json.load(open("artifacts/release.json"))["isDraft"]).lower())')"
if [[ "$draft" == true ]]; then
  python3 - <<'PY'
import json, os
release = json.load(open('artifacts/release.json'))
if release['targetCommitish'] != os.environ['GITHUB_SHA']:
    raise SystemExit('Existing draft belongs to another commit; rerun its original workflow.')
PY
  gh release view "$tag" --repo "$GITHUB_REPOSITORY" --json assets > artifacts/assets.json
  action="$(python3 scripts/distribution.py draft-asset-action --assets artifacts/assets.json --name "$archive")"
  if [[ "$action" == verify ]]; then
    gh release download "$tag" --repo "$GITHUB_REPOSITORY" --pattern "$archive" \
      --dir artifacts/published --clobber
    cmp "artifacts/$archive" "artifacts/published/$archive"
  else
    if [[ "$action" == replace-incomplete ]]; then
      gh release delete-asset "$tag" "$archive" --repo "$GITHUB_REPOSITORY" --yes
    fi
    gh release upload "$tag" "artifacts/$archive" --repo "$GITHUB_REPOSITORY"
  fi
  gh release edit "$tag" --repo "$GITHUB_REPOSITORY" --draft=false
else
  echo "Version ${version} is already published; preserving its original assets."
fi

gh release download "$tag" --repo "$GITHUB_REPOSITORY" --pattern "$archive" \
  --dir artifacts/published --clobber
gh release view "$tag" --repo "$GITHUB_REPOSITORY" \
  --json isDraft,targetCommitish,publishedAt,body,assets > artifacts/release.json
python3 scripts/distribution.py verify --zip "artifacts/published/$archive" --release artifacts/release.json --built-zip "artifacts/$archive"
