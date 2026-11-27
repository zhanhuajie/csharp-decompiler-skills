# csharp-decompiler-skills

[中文](README.zh-CN.md)

A collection of agent-agnostic AI skills for .NET API metadata indexing, symbol/XML-doc lookup, and IL decompilation. Designed for analyzing closed-source SDKs (such as Revit, AutoCAD, Plant 3D, or NuGet packages) and reverse-engineering business logic.

## Skills

| Skill | Description |
| --- | --- |
| [csharp-definition-lookup](csharp-definition-lookup/SKILL.md) | Offline .NET API indexer (`System.Reflection.Metadata` + SQLite FTS5). Full-text, CamelCase & CJK search across types, members, and XML docs; clean C# API skeletons (nullable annotations, `ref`/`readonly` structs, inherited class/interface members & constrained generic extensions); type hierarchy, reverse type lookup, and `[TypeForwardedTo]` locator. |
| [csharp-decompiler](csharp-decompiler/SKILL.md) | Decompile .NET IL assemblies (`.dll`, `.exe`) to C# source code via `ilspycmd`. Use when method bodies, internal side effects, or full project export are needed. |

---

## Getting Started

### Prerequisites

- [.NET 8+ SDK / Runtime](https://dotnet.microsoft.com/download) installed
- **ilspycmd** installed (for `csharp-decompiler`):
  ```bash
  dotnet tool install -g ilspycmd
  ```

### Build / Publish `apilookup` (Optional — Windows binary is prebuilt)

```powershell
# Build and publish framework-dependent single-file binary into csharp-definition-lookup/scripts/
.\tools\build.ps1

# Run unit tests
dotnet test .\ApiLookup.sln -c Release
```

> The build embeds a line-ending-normalized hash of the sources (`apilookup --version --verbose` → `source-hash:`). CI fails if the committed `scripts/apilookup.exe` hash differs from a fresh build, so re-run `.\tools\build.ps1` and commit the binary after every source change.

Cross-platform & global tool options:
- Run from source: `dotnet run --project src/ApiLookup -- <args>`
- Publish single-file: `.\tools\build.ps1 -Runtime linux-x64` (or `osx-arm64`)
- Install as `.NET Tool`: `dotnet pack src/ApiLookup -c Release -o artifacts && dotnet tool install -g --add-source artifacts ApiLookup`

---

## csharp-definition-lookup (`apilookup`)

Powered by `System.Reflection.Metadata` + SQLite FTS5. Indexes managed assemblies (and companion `.xml` documentation files) into a fast local cache without loading DLLs or decompiling noisy IL.

### Build a named SDK index

```powershell
# AutoCAD + Plant 3D (filter to Autodesk / PnP DLLs)
.\csharp-definition-lookup\scripts\apilookup.exe index --name acad2025 --no-recursive `
  --dir "C:\Program Files\Autodesk\AutoCAD 2025" `
  --dir "C:\Program Files\Autodesk\AutoCAD 2025\PLNT3D" `
  --include "ac*mgd.dll" --include "Ac*.dll" --include "Autodesk.*.dll" --include "PnP*.dll" --include "PnId*.dll"

# Revit (automatically indexes RevitAPI.xml & RevitAPIUI.xml)
.\csharp-definition-lookup\scripts\apilookup.exe index --name revit2026 `
  --dll "C:\Program Files\Autodesk\Revit 2026\RevitAPI.dll" `
  --dll "C:\Program Files\Autodesk\Revit 2026\RevitAPIUI.dll"
```

### Query examples

```powershell
# Full-text, CamelCase & CJK search across names and XML summaries
apilookup.exe search "offscreen device" --index acad2025
apilookup.exe search "Wall.Create" --index revit2026

# Clean C# skeleton with inherited class/interface members, extension methods, and XML docs
apilookup.exe show DocumentCollection --index acad2025 --ns Autodesk.AutoCAD --ext --member Open
apilookup.exe show Equipment --index acad2025 --ns Autodesk.ProcessPower.PnP3dObjects --member Port --inherited
apilookup.exe show Wall --index revit2026 --member Create --docs

# Type hierarchy & reverse type lookup (supports simple or namespace-qualified type names)
apilookup.exe hierarchy Autodesk.ProcessPower.PnP3dObjects.Part --index acad2025
apilookup.exe by-type DataLinksManager3d --index acad2025

# Fast zero-cache scan to locate which DLL defines or forwards a type
apilookup.exe locate Equipment --project C:\repo\MyApp\MyApp.csproj --list-refs

# Ad-hoc search against a .csproj (expands Directory.Build.props macros & auto-caches by fingerprint)
apilookup.exe search Asset --project C:\repo\MyApp\MyApp.csproj --list-refs
```

---

## csharp-decompiler (`ilspycmd`)

Use when signatures and XML docs aren't enough and you need the actual method implementation:

```powershell
# Windows (PowerShell — ilspycmd writes C# to stdout and version notices to stderr; pipe stdout as UTF-8)
ilspycmd -t <FullNamespace.ClassName> <assembly_path> | Out-File -FilePath "$env:TEMP\MyType.cs" -Encoding utf8

# Full project export
ilspycmd -p -o <output_directory> <assembly_path>
```

---

## Recommended Workflow

```text
Need API / type / usage
        │
        ▼
csharp-definition-lookup (apilookup)  ──►  FTS5 search, C# skeleton, XML docs,
        │                                  inheritance, extensions, exact DLL path
        ▼ (only when method body / internal side effects matter)
csharp-decompiler (ilspycmd)          ──►  Full C# decompiled implementation
```

---

## Repository Structure

```text
ApiLookup.sln
Directory.Build.props
.github/workflows/ci.yml
src/
  ApiLookup/               # .NET 8 CLI (System.Reflection.Metadata + Microsoft.Data.Sqlite FTS5)
    Cli/                   # CLI option model
    Extraction/            # Metadata, constants/nullability, signature decoder, tokenizer & source resolver
    Formatting/            # Text/JSON formatters + camelCase System.Text.Json source-gen context
    Models/                # Extracted & query DTO records
    Storage/               # Index catalog, async streaming builder, indexed SQLite query store
  ApiLookup.Tests/         # xUnit unit & integration tests
tools/
  build.ps1                # Publishes apilookup binary to csharp-definition-lookup/scripts/
csharp-definition-lookup/
  SKILL.md                 # Skill guide for AI agents
  scripts/
    apilookup.exe          # Prebuilt Windows single-file CLI (framework-dependent)
csharp-decompiler/
  SKILL.md                 # Skill guide for AI agents
```

---

## License

[MIT](LICENSE)
