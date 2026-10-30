#!/usr/bin/env bash
# New releases are drafts until assets are uploaded. Retries reuse published bytes.
set -euo pipefail
version="$(python3 scripts/distribution.py version)"
tag="v${version}"
archive="ASS-Fonts_${version}.zip"
mkdir -p artifacts/published

# Same-version replacement is an explicit manual workflow action.
if [[ "${REPLACE_EXISTING_RELEASE:-false}" == true ]]; then
  python3 - <<'PY'
import json
from scripts.distribution import read_version
catalog = json.load(open('plugin-index/manifest.json'))
if any(v['version'] == read_version()
       for p in catalog if p['guid'].lower() == 'cb963c2d-9d76-48b1-9f90-5c0cd5b25eae'
       for v in p['versions']):
    raise SystemExit('Reset the current version in the catalog before replacing its release.')
PY
  head="$(gh api "repos/$GITHUB_REPOSITORY/git/ref/heads/main" --jq .object.sha)"
  [[ "$head" == "$GITHUB_SHA" ]] || { echo 'Replacement must use the current main commit.' >&2; exit 1; }
  gh release view "$tag" --repo "$GITHUB_REPOSITORY" --json isDraft > artifacts/release.json
  gh api --method PATCH "repos/$GITHUB_REPOSITORY/git/refs/tags/$tag" \
    -f sha="$GITHUB_SHA" -F force=true > /dev/null
  gh release upload "$tag" "artifacts/$archive" \
    --repo "$GITHUB_REPOSITORY" --clobber
  gh release edit "$tag" --repo "$GITHUB_REPOSITORY" --draft=false \
    --target "$GITHUB_SHA" --notes 'Subset font embedding for ASS/SSA subtitles served by Jellyfin.'
fi

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
python3 scripts/distribution.py verify --zip "artifacts/published/$archive" --release artifacts/release.json
