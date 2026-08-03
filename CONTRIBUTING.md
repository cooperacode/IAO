# Contributing

## Branches and pull requests

Use short-lived branches for each change:

```text
feat/context-usage
fix/timeout-recovery
docs/versioning
```

Open a pull request against `main` and wait for the checks for all four engines.
The `main` branch should always remain releasable.

## Commits

Use [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/):

```text
feat: add context usage tracking
fix: recover from timeout
docs: explain the release flow
refactor: simplify feature store
test: cover invalid envelopes
chore: update dependencies
```

For breaking changes, use `!` or a `BREAKING CHANGE` footer:

```text
feat!: change the envelope contract
```

## Versions and releases

The official version is stored in [`VERSION`](VERSION). It follows Semantic
Versioning:

- `MAJOR`: incompatible protocol or public contract change;
- `MINOR`: new backwards-compatible feature;
- `PATCH`: backwards-compatible fix.

The Python and Rust manifests must match the version in `VERSION`. Run the
validation locally with:

```bash
bash scripts/check-version.sh
```

To publish a release:

1. Update `VERSION`.
2. Move the changes from `Unreleased` into the new section in `CHANGELOG.md`.
3. Merge the change into `main`.
4. Create an annotated tag with the same version:

   ```bash
   git tag -a v0.1.0 -m "Release v0.1.0"
   git push origin v0.1.0
   ```

The release workflow validates the tag, runs the checks, and publishes Linux
packages for all four engines as GitHub Release assets.
