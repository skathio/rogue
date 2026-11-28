---
name: release-prep
description: Prepare a SkathIO.Rogue release PR (PublicAPI promotion, benchmark baseline, CHANGELOG, dependency claims) before the maintainer dispatches publish.yml. Use when asked to prepare, cut or check readiness for a release.
---

# Release prep

Releases go out from `.github/workflows/publish.yml`, dispatched by the maintainer with `bump` set
to `patch`, `minor` or `major` and approved in the `production` environment. Prepare a PR that
makes `main` ready. **Never dispatch the workflow, tag, or push to nuget.org yourself.**
The source of these steps is `docs/governance.md`, "Release readiness".

1. **Choose the bump.** Find the last tag (`git describe --tags --abbrev=0`; tags are `vX.Y.Z`) and
   read `## [Unreleased]` in `CHANGELOG.md`. Removed or changed public API means `major`, added API
   means `minor`, fixes and dependency updates mean `patch`. Recommend a bump; the maintainer decides.

2. **Promote public API.** For every packable project whose `PublicAPI.Unshipped.txt` has entries,
   move them to `PublicAPI.Shipped.txt` (one pair per project, at the project root). This freezes
   that API for good, so list every promoted line in the PR description.

3. **Benchmarks: required before every release** (`docs/governance.md`). Run the full suite on a
   quiet machine: `dotnet run -c Release --project bench/SkathIO.Rogue.Benchmarks -- --filter '*'`,
   and also `bench/SkathIO.Rogue.Benchmarks.Validation` with the same arguments.
   This takes a long time; if you cannot run it, say so in the PR and leave it to the maintainer.
   Commit the run as `bench/results/<YYYY-MM-DD>-<short-sha>/` and add a `!bench/results/<dir>/`
   line to `.gitignore`, or the run is ignored. Update `bench/RESULTS.md`, `docs/benchmarks.md`, and
   the stats in `site/index.html` and `README.md` if the numbers changed. A site change needs the
   `visual-test` skill.

4. **CHANGELOG.** Move `[Unreleased]` content into `## [X.Y.Z] - YYYY-MM-DD` and leave an empty
   `## [Unreleased]` above it.

5. **Check every claim against the branch.** For each package version, SDK floor or compatibility
   claim in the new entry, `grep` `Directory.Packages.props` at the commit being released. Do not
   rely on the CHANGELOG text.

6. **Verify locally:** `dotnet build SkathIO.Rogue.sln -c Release` (zero warnings) and
   `dotnet test SkathIO.Rogue.sln -c Release --no-build`.

7. Open the PR as `chore: release X.Y.Z prep` or `docs(changelog): …`, state the recommended bump,
   wait for green CI, and hand off. The maintainer merges and dispatches `publish.yml`. Tell the
   maintainer that the site built by its `site-check` job is kept for one day: if `publish` is
   approved later, the packages and the tag still go out and only the `site` job fails, so run
   `site.yml` by hand, approved in `production`.
