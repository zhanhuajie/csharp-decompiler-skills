---
name: csharp-definition-lookup
description: Fast offline .NET API metadata & XML-doc lookup powered by System.Reflection.Metadata + SQLite FTS5. Use to search types/members/XML docs across .csproj references, NuGet packages, or SDK directories (Revit, AutoCAD, Plant 3D, etc.), inspect clean C# API skeletons (with inherited members and extension methods), explore type hierarchies, and reverse-lookup methods returning/accepting a type. Use csharp-decompiler only when you need method bodies or internal implementation details.
---

# C# Definition & API Lookup

Offline .NET metadata & XML documentation indexer (`System.Reflection.Metadata` + SQLite FTS5). Extracts clean C# signatures, inheritance chains, extension methods, enum values, and companion `.xml` docs directly from PE assemblies—without loading them into a host process or decompiling noisy IL.

## Tool Location

- **Windows (ready to run, requires .NET 8+ runtime)**: `<skill_dir>/scripts/apilookup.exe`
- **Cross-platform / from source**: `dotnet run --project <repo>/src/ApiLookup -- <args>`
- **Rebuild binary**: `<repo>/tools/build.ps1`

No `dotnet-script` required.

## Core Commands

| Command | Purpose |
| --- | --- |
| `search <query>` | Full-text + CamelCase search across types, members, and XML `<summary>` docs |
| `show <TypeName>` | Print clean C# API skeleton (constructors, properties, methods, events, fields/enums) |
| `hierarchy <TypeName>` | Show base class chain, interfaces, and all derived/implementing types |
| `by-type <TypeName>` | Reverse-lookup members returning or accepting `<TypeName>` |
| `index` | Build/refresh a named index (`--name`), list indexes (`--list`), or delete (`--drop`) |
| `refs` | List indexed assemblies, versions, type counts, and XML doc availability |

## Specifying Sources (Auto-Cached)

Every command accepts any combination of `--dir`, `--project`, `--dll`, or a previously built `--index <name>`:

- `--dir <path>` (repeatable): Recursively scan managed `.dll`s (automatically skips native-only binaries, `*.resources.dll`, and locale folders). Pair with `--include <glob>` / `--exclude <glob>` (e.g. `--exclude "DevExpress.*"`) to keep large SDK directories small and fast.
- `--project <path.csproj>` (repeatable): Resolves NuGet packages via `obj/project.assets.json` (run `dotnet restore` first) plus `<HintPath>` references.
- `--dll <path.dll>` (repeatable): Index specific assembly files directly.
- `--index <name>` (alias `--name`): Build once with `index --name <name> ...`, then reuse instantly (`<50 ms`) across queries.
- Ad-hoc `--dir`/`--project`/`--dll` queries are **auto-cached** by file size + mtime fingerprint in `%LOCALAPPDATA%\csharp-api-lookup\indexes\` (or `~/.cache/csharp-api-lookup/indexes/`).

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
apilookup.exe search "create ceiling" --index revit2026
apilookup.exe search Asset --project C:\repo\MyApp\MyApp.csproj --ns Autodesk
```

CamelCase identifiers are automatically tokenized (`CreateAutoCADOffScreenDevice` matches `"offscreen"`, `"off screen"`, `"autocad"`, `"device"`). Filter with `--kind type|method|property|enum|ctor`, `--ns <prefix>`, `--asm <fragment>`, `--limit <n>`.

### 3. Inspect a type's clean C# API surface

```powershell
# Filter members + include inherited base members
apilookup.exe show Equipment --index acad2025 --ns Autodesk.ProcessPower.PnP3dObjects --member Port --inherited

# Include extension methods targeting this type or its base classes/interfaces
apilookup.exe show DocumentCollection --index acad2025 --ns Autodesk.AutoCAD --ext --member Open

# Include full XML @param / @returns / Remarks docs
apilookup.exe show Wall --index revit2026 --member Create --docs
```

### 4. Explore inheritance & discover factory/helper methods

```powershell
# See all subclasses of Part (Pipe, Equipment, PipeInlineAsset, Connector, Support...)
apilookup.exe hierarchy Autodesk.ProcessPower.PnP3dObjects.Part --index acad2025

# Find how to obtain or pass a DataLinksManager3d
apilookup.exe by-type DataLinksManager3d --index acad2025
apilookup.exe by-type Wall --index revit2026 --role return --limit 20
```

## When to Escalate to `csharp-decompiler`

Use `csharp-definition-lookup` **first** for:
- Finding which DLL and namespace a type lives in
- Checking method signatures, overloads, optional parameters, enums, XML docs
- Discovering inherited members, extension methods, subclasses, and factory methods

Escalate to `csharp-decompiler` (`ilspycmd -t <FullName> <DllPath>`) **only when you need method implementation details** (e.g., hidden side effects, native interop preconditions, or internal error-dialog class names). `show` prints the exact `Assembly` path and `CLR Name` needed by `ilspycmd`.

## Tips & Notes

- **Use `--member <substring>`** on huge classes (like `Database`, `Document`, `UIDocument`) to keep output concise.
- **Filter large install directories** with `--include` / `--exclude` (e.g. `--exclude "DevExpress.*"`) to avoid indexing hundreds of bundled third-party UI DLLs.
- **Non-public members**: By default only `public` and `protected` APIs are indexed (which also strips C++/CLI native internal structs). Pass `--include-nonpublic` if reverse-engineering internal types.
- **Structured output**: Any command supports `--json`.
- **Legacy script**: `scripts/TypeScanner.csx` is retained for backwards compatibility, but `apilookup.exe` supersedes it.
