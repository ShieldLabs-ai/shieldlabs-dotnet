# Contributing

Thanks for helping improve the ShieldLabs .NET SDK. Questions about the API itself go to
[contact@shieldlabs.ai](mailto:contact@shieldlabs.ai); bugs and pull requests are welcome here.

## Set up

You need the .NET 8 SDK, or Docker with the `mcr.microsoft.com/dotnet/sdk:8.0` image.

```sh
dotnet build -c Release -warnaserror
dotnet test -c Release --no-build
```

Warnings are errors in every project. The test suite must pass before a change is merged. CI also
runs it on .NET 9 and 10 and against the `netstandard2.0` build of the library:

```sh
dotnet build -c Release -warnaserror -p:TestLibraryTargetFramework=netstandard2.0
dotnet test -c Release --no-build -p:TestLibraryTargetFramework=netstandard2.0
```

## Layout

| Path | Contents |
|---|---|
| `src/ShieldLabs` | The package. Public types live at the top level; `Internal/` holds implementation details. |
| `tests/ShieldLabs.Tests` | xUnit tests with a scripted `HttpMessageHandler` and a virtual clock (no network, no real waits). |
| `tests/ShieldLabs.Tests/data` | Shared test fixtures that every ShieldLabs server SDK passes. Do not edit them by hand: they change together with the public API. |
| `examples/MinimalApi` | ASP.NET Core example, built in CI. |

## Guidelines

- Keep the public API small and documented: every public member has XML documentation.
- New behaviour needs tests. Use the fixtures for anything that touches the wire format.
- Parse server responses defensively: unknown fields, unknown enum values and missing optional
  fields must never throw.
- Never log keys, secrets or request and response bodies.
- Use plain, technical English in docs and comments.
- Commit messages follow Conventional Commits (`feat:`, `fix:`, `docs:`, `test:`, `ci:`).

## Releases

Maintainers bump `<Version>` in `src/ShieldLabs/ShieldLabs.csproj` (the `User-Agent` version is read
from it), add a `CHANGELOG.md` entry and push a tag `v<version>`. The release workflow checks that
the tag matches `<Version>`, builds, tests and packs, then publishes to nuget.org from the `nuget`
environment with a build provenance attestation. Re-running it is safe: versions already on
nuget.org are skipped.

One-time setup:

- On nuget.org, reserve the `ShieldLabs` package ID prefix and add a trusted publishing policy for
  this repository, the workflow `release.yml` and the environment `nuget`.
- In the repository settings, create the `nuget` environment with required reviewers and add the
  secret `NUGET_USER` (the nuget.org profile name that owns the policy). No long-lived API key is
  stored.
