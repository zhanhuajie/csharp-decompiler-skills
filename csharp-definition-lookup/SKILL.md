---
name: csharp-definition-lookup
description: Fast offline .NET API metadata & XML-doc lookup powered by System.Reflection.Metadata + SQLite FTS5. Use to search types/members/XML docs across .csproj references, NuGet packages, or SDK directories (Revit, AutoCAD, Plant 3D, etc.), inspect clean C# API skeletons (with inherited members and extension methods), explore type hierarchies, and reverse-lookup methods returning/accepting a type. Use csharp-decompiler only when you need method bodies or internal implementation details.
---

# C# Definition & API Lookup

Offline .NET metadata & XML documentation indexer (`System.Reflection.Metadata` + SQLite FTS5). Extracts clean C# signatures (including nullable annotations, `ref`/`readonly` structs, `ref` returns, `[Flags]` & `decimal` constants), class/interface inheritance chains, extension methods (including constrained generic extensions), enum values, type forwarders, and companion `.xml` docs directly from PE assemblies—without loading them into a host process or decompiling noisy IL.

## Tool Location

- **Windows (ready to run, requires .NET 8+ runtime)**: `<skill_dir>/scripts/apilookup.exe`
- **.NET Global/Local Tool**: `dotnet pack <repo>/src/ApiLookup -c Release` then `dotnet tool install -g --add-source <nupkg_dir> ApiLookup` (`apilookup`)
- **Cross-platform / from source**: `dotnet run --project <repo>/src/ApiLookup -- <args>`
- **Rebuild binary**: `<repo>/tools/build.ps1`

No `dotnet-script` required.

## Core Commands

| Command | Purpose |
| --- | --- |
| `search <query>` | Full-text, CamelCase & CJK/Chinese search across types, members, and XML `<summary>` docs |
| `show <TypeName>` | Print clean C# API skeleton (constructors, properties, methods, events, fields/enums, delegates) |
| `hierarchy <TypeName>` | Show base class/interface chain, interfaces, and all derived/implementing types |
| `by-type <TypeName>` | Reverse-lookup members returning or accepting `<TypeName>` (supports full namespace or simple name, including inside generic containers) |
| `locate <TypeName>` | Fast zero-cache PE scan (including `[TypeForwardedTo]` facades) to locate a type's exact DLL & namespace (alias: `scan`) |
| `index` | Build/refresh a named index (`--name`), list (`--list`), delete (`--drop`), or prune auto-cache (`--prune`) |
| `refs` | List indexed assemblies, versions, type counts, and XML doc availability |

## Specifying Sources (Auto-Cached)

Every command accepts any combination of `--dir`, `--project`, `--dll`, or a previously built `--index <name>`:

- `--dir <path>` (repeatable): Recursively scan managed `.dll` and `.exe` assemblies (automatically skips native-only binaries, `*.resources.dll`, and locale folders). Pair with `--include <glob>` / `--exclude <glob>` (e.g. `--exclude "DevExpress.*"`) to keep large SDK directories small and fast.
- `--project <path.csproj>` (repeatable): Resolves NuGet packages via `obj/project.assets.json` (run `dotnet restore` first), project references (honoring custom `<AssemblyName>`), plus `<HintPath>` references (expanding `$(ProjectDir)`, `$(SolutionDir)`, and MSBuild properties from `.csproj` / ancestor `Directory.Build.props`). For multi-TFM projects, automatically selects the highest framework or pass `--tfm <tfm>` (e.g. `--tfm net8.0`).
- `--dll <path.dll>` (repeatable): Index specific assembly files directly.
- `--index <name>` (alias `--name`): Build once with `index --name <name> ...`, then reuse instantly (`<50 ms`) across queries.
- Ad-hoc `--dir`/`--project`/`--dll` queries are **auto-cached** by file size + mtime fingerprint in `%LOCALAPPDATA%\csharp-api-lookup\indexes\` (or `~/.cache/csharp-api-lookup/indexes/`, overridable via `APILOOKUP_CACHE_DIR`). Run `apilookup.exe index --prune` to clean up old `auto-*.db` files.

## Recommended Workflows

### 1. Build a reusable SDK index once (best for Revit / AutoCAD / Plant 3D)

```powershell
# AutoCAD + Plant 3D (filtered to Autodesk / PnP assemblies)
apilookup.exe index --name acad2025 --no-recursive `
  --dir "C:\Program Files\Autodesk\AutoCAD 2025" `
  --dir "C:\Program Files\Autodesk\AutoCAD 2025\PLNT3D" `
  --include "ac*mgd.dll" --include "Ac*.dll" --include "Autodesk.*.dll" --include "PnP*.dll" --include "PnId*.dll"

# Revit (automatically loads companion RevitAPI.xml & RevitAPIUI.xml)
apilookup.exe index --name revit2026 `
  --dll "C:\Program Files\Autodesk\Revit 2026\RevitAPI.dll" `
  --dll "C:\Program Files\Autodesk\Revit 2026\RevitAPIUI.dll"
```

### 2. Search when you don't know the exact class or method name

```powershell
apilookup.exe search "offscreen device" --index acad2025
apilookup.exe search "Wall.Create" --index revit2026
apilookup.exe search Asset --project C:\repo\MyApp\MyApp.csproj --ns Autodesk
```

CamelCase identifiers are automatically tokenized (`CreateAutoCADOffScreenDevice` matches `"offscreen"`, `"off screen"`, `"autocad"`, `"device"`). Filter with `--kind type|method|property|enum|ctor`, `--ns <prefix>`, `--asm <fragment>`, `--limit <n>`.

### 3. Inspect a type's clean C# API surface

```powershell
# Filter members + include inherited base class / parent interface members
apilookup.exe show Equipment --index acad2025 --ns Autodesk.ProcessPower.PnP3dObjects --member Port --inherited

# Include extension methods targeting this type, its base classes/interfaces, or generic constraints
apilookup.exe show DocumentCollection --index acad2025 --ns Autodesk.AutoCAD --ext --member Open

# Include full XML @param / @returns / Remarks docs
apilookup.exe show Wall --index revit2026 --member Create --docs
```

### 4. Explore inheritance & discover factory/helper methods

```powershell
# See all subclasses / implementers of Part (Pipe, Equipment, PipeInlineAsset, Connector, Support...)
apilookup.exe hierarchy Autodesk.ProcessPower.PnP3dObjects.Part --index acad2025

# Find how to obtain or pass a DataLinksManager3d (supports simple or namespace-qualified type names)
apilookup.exe by-type DataLinksManager3d --index acad2025
apilookup.exe by-type Autodesk.Revit.DB.Wall --index revit2026 --role return --limit 20
```

## When to Escalate to `csharp-decompiler`

Use `csharp-definition-lookup` **first** for:
- Finding which DLL and namespace a type lives in (including `[TypeForwardedTo]` facade redirects)
- Checking method signatures, overloads, optional parameters, enums, XML docs
- Discovering inherited members, extension methods, subclasses, and factory methods

Escalate to `csharp-decompiler` (`ilspycmd -t <FullName> <DllPath>`) **only when you need method implementation details** (e.g., hidden side effects, native interop preconditions, or internal error-dialog class names). `show` prints the exact `Assembly` path and `CLR Name` needed by `ilspycmd`.

## Tips & Notes

- **Use `--member <substring>`** on huge classes (like `Database`, `Document`, `UIDocument`) to keep output concise.
- **Filter large install directories** with `--include` / `--exclude` (e.g. `--exclude "DevExpress.*"`) to avoid indexing hundreds of bundled third-party UI DLLs.
- **Non-public members**: By default only `public` and `protected` APIs are indexed (which also strips C++/CLI native internal structs). Pass `--include-nonpublic` if reverse-engineering internal types.
- **Structured output**: Any command supports `--json` (emitting consistent `camelCase` JSON, including structured `{ "error", "exitCode" }` envelopes on failure).
- **Diagnostics & Version**: Pass `--verbose` to inspect metadata/XML warnings on stderr, and `--version` to check the CLI version.
- **Exit codes**: `0` success, `1` error or type not found, `2` ambiguous type match in `show`/`hierarchy` (candidates printed to stdout).
- **Zero-cache DLL scan**: Use `locate <TypeName>` (alias `scan`) with `--project` / `--dir` / `--dll` when you only want to find which DLL defines or forwards a type without creating a SQLite index.
