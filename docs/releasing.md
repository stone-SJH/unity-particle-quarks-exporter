# Release Process

The runtime release is deliberately split into two manual gates. Use a
GitHub-hosted runner and configure npm trusted publishing for organization/user
`stone-SJH`, repository `unity-particle-quarks-exporter`, workflow filename
`npm-release.yml`, and GitHub environment `npm`. The workflow uses Node 24 and
an OIDC-enabled npm CLI; `npm publish` prefers its short-lived OIDC identity.

The first publication is a bootstrap exception because npm cannot attach a
trusted publisher until the package exists. Put a short-lived granular publish
token in the protected `npm` environment as `NPM_TOKEN`, run `publish-next`,
then configure the trusted publisher and remove that bootstrap token. If npm
requires interactive 2FA for the first claim, publish the already verified
tarball locally with an owner account and an OTP, then run the registry install
verification before configuring OIDC.

Before publishing, run the `unity-contract` workflow for the release commit on
a Windows runner labeled `unity` with both declared Unity editors installed.
All four editor/pipeline matrix jobs must pass and retain their XML, log, and
manifest evidence.

1. Run the `npm-release` workflow with action `publish-next` and the exact
   version from `packages/particle-quarks-runtime/package.json`. The workflow
   verifies source, the Unity-exported runtime lifecycle, Chromium loading, and
   a clean tarball installation before publishing the tarball under `next`. It
   then installs that exact version back from the public npm registry.
2. Inspect the `next` result and provenance. Run the same workflow with action
   `promote-latest` and the same version. The workflow repeats all checks,
   installs the exact registry artifact, promotes it to `latest`, and verifies
   the resulting dist-tag.

OIDC currently authenticates `npm publish`, but not `npm dist-tag add`.
`promote-latest` therefore requires a short-lived granular `NPM_TOKEN` in the
protected environment; remove it after promotion. `npm whoami` is deliberately
not used for the OIDC path because npm only exchanges that identity during the
publish operation.

The workflow refuses to overwrite an existing version. If npm rejects the
unscoped name because the publishing account does not own it, choose an
organization scope and update the package name, workspace scripts, imports,
documentation, and release workflow together before retrying.
