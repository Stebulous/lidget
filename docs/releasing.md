# Release Lidget

Use this runbook to publish a new version of Lidget on GitHub Releases.
Installed copies of Lidget update only from published releases. A draft release does not reach users.

> [!WARNING]
> A published release goes to every user within 24 hours, and Velopack has no rollback.
> An agent must not push a tag or publish a release without explicit permission from the maintainer.

## Prerequisites

- Push access to `Stebulous/lidget`, and the GitHub CLI (`gh`), signed in.
- A clean `main` branch, in sync with `origin/main`, with green CI.
- The checks in [the QA checklist](qa-checklist.md), done on an installer of the release candidate.

## Steps

1. Choose the version. Use semantic versioning, for example `0.2.0`.
2. Change `<Version>` in `Directory.Build.props` to the new version, and commit the change.
   The release workflow uses the tag as the version. This value is for local builds and the Settings window of a Debug build.
3. Create an annotated tag on the release commit, and push it:

   ```powershell
   git tag -a v0.2.0 -m "Lidget 0.2.0"
   git push origin v0.2.0
   ```

4. Wait for the **Release** workflow in `.github/workflows/release.yml` to finish.
   The workflow runs the tests, packs the release with `scripts/pack.ps1`, and uploads it to a draft release.
   The workflow also downloads the previous release, so that Velopack can make a delta package.
5. Download `Lidget-win-Setup.exe` from the draft release, install it, and check that the widget shows live data.
6. Write the release notes in the draft. List the user-visible changes and the install steps.
7. Publish the draft:

   ```powershell
   gh release edit v0.2.0 --draft=false --latest
   ```

Expected result: the release page shows **Latest**, with these assets:

- `Lidget-win-Setup.exe`
- `Lidget-0.2.0-full.nupkg`, and a delta package after the first release
- `Lidget-win-Portable.zip`
- `RELEASES` and `releases.win.json`

Within 24 hours, installed copies download the update and install it at the next start.

## If the workflow fails

- If the tests fail, correct the cause on `main`. Delete the tag, and then tag the new commit.
- If the upload fails, start the workflow again from the **Actions** page of the repository.
- Do not publish a draft that has missing assets.

## If a published release is bad

Publish a new release with a higher version that corrects the problem.
Do not delete a published release: installed copies that already updated need a newer version to update again.
