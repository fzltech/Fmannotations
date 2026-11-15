# Fmverify.Annotations

The annotation attribute library and Roslyn predicate analyzer for **fmverify** — developed and released independently from the fmverify CLI.

## What this is

`Fmverify.Annotations` is the only piece of fmverify that ends up referenced by adopter target code. It provides the attributes adopters annotate their own code with so the fmverify conformance checker can cross-check the hand-written bridge against what the code declares about itself:

| Attribute | Targets | Meaning |
|-----------|---------|---------|
| `[Step("name")]` | Method | Declares an observable transition of the slice under verification |
| `[Deterministic]` | Method | Driving the seam twice on the same scenario produces the same result |
| `[Requires("...")]` | Method | Precondition that must hold where the seam is entered |
| `[Ensures("...")]` | Method | Postcondition that must hold after the seam runs |
| `[Invariant("...")]` | Class / Struct | Predicate that must hold on each observed instance of the type |
| `[Measure("...")]` | Class / Struct / Property / Field / Method | Integer quantity that must fall as the run proceeds |

The checker matches attribute types by full type name, so attributes arrive identically whether imported via the NuGet package or copied as a standalone source file (`fmverify add-annotations --source`).

## Distribution

The package is published to GitHub Packages under the `fzltech` org:

```
https://nuget.pkg.github.com/fzltech/index.json
```

Restore requires the GitHub Packages source configured and a token with `read:packages`.

Adopters reference it via `fmverify add-annotations <target> --apply`, which writes:

```xml
<PackageReference Include="Fmverify.Annotations" Version="0.5.0" />
```

For air-gapped or feedless environments, `fmverify add-annotations <target> --source` copies the standalone `Annotations.cs` file directly into the target project.

## Package contents

- `lib/netstandard2.0/Fmverify.Annotations.dll` — the attribute library
- `lib/net8.0/Fmverify.Annotations.dll`
- `lib/net9.0/Fmverify.Annotations.dll`
- `lib/net10.0/Fmverify.Annotations.dll`
- `analyzers/dotnet/cs/Fmverify.Annotations.Analyzers.dll` — the Roslyn predicate analyzer (`FMV0001`–`FMV0007`), included only in the package (not in the source copy)

## Versioning

This repository's `VERSION` file is the single home for the published package version. The release workflow bumps it on semantic-version-labeled merges to `main`.

The annotation allowance's own version (`Allowance.Version`, currently `2026.09.2`) lives in `src/Fmverify.Annotations/Annotations.cs` and is compared by fmverify's generated conformance checker on every gate run. Bump it whenever an attribute's shape or semantics change.

## Relationship to fmverify

fmverify consumes this package and ships a committed copy of `Annotations.cs` as an embedded resource for `add-annotations` source mode. The two repositories version independently:

| | Fmannotations | fmverify |
|---|---|---|
| What's here | Attribute library + analyzer + analyzer tests | CLI tool, generated layer scaffold |
| Released as | `Fmverify.Annotations` NuGet package | `fmverify` tool (from source/local pack) |
| Consumed by | Adopter target code, fmverify tests | Adopters' verification layers |

See `docs/STATUS.md` in the fmverify repository for the current distribution decision (package-first, source-copy fallback).

## Building

```bash
dotnet build fmverify.slnx -c Release
dotnet pack src/Fmverify.Annotations/Fmverify.Annotations.csproj -c Release -o .packages/
```

Or use the convenience scripts:

```bash
bash pack-annotations.sh      # Linux / macOS
pwsh pack-annotations.ps1     # Windows
```

## License

MIT — see `Fmverify.Annotations/Fmverify.Annotations.csproj` (`PackageLicenseExpression`).
