# csharp-decompiler-skills

[中文](README.zh-CN.md)

A collection of agent-agnostic AI skills for .NET API metadata indexing, symbol/XML-doc lookup, and IL decompilation. Designed for analyzing closed-source SDKs (such as Revit, AutoCAD, Plant 3D, or NuGet packages) and reverse-engineering business logic.

## Skills

| Skill | Description |
| --- | --- |
| [csharp-definition-lookup](csharp-definition-lookup/SKILL.md) | Offline .NET API indexer (`System.Reflection.Metadata` + SQLite FTS5). Full-text & CamelCase search across types, members, and XML docs; clean C# API skeletons with inherited members & extension methods; type hierarchy and reverse type lookup. |
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

On Linux/macOS, invoke directly via `dotnet run --project src/ApiLookup -- <args>` or publish with `-r linux-x64` / `-r osx-arm64`.

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
# Full-text & CamelCase search across names and XML summaries
apilookup.exe search "offscreen device" --index acad2025
apilookup.exe search "create ceiling" --index revit2026

# Clean C# skeleton with inherited members, extension methods, and XML docs
apilookup.exe show DocumentCollection --index acad2025 --ns Autodesk.AutoCAD --ext --member Open
apilookup.exe show Equipment --index acad2025 --ns Autodesk.ProcessPower.PnP3dObjects --member Port --inherited
apilookup.exe show Wall --index revit2026 --member Create --docs

# Type hierarchy & reverse type lookup
apilookup.exe hierarchy Autodesk.ProcessPower.PnP3dObjects.Part --index acad2025
apilookup.exe by-type DataLinksManager3d --index acad2025

# Ad-hoc search against a .csproj (auto-cached by fingerprint)
apilookup.exe search Asset --project C:\repo\MyApp\MyApp.csproj --list-refs
```

---

## csharp-decompiler (`ilspycmd`)

Use when signatures and XML docs aren't enough and you need the actual method implementation:

```powershell
# Windows (PowerShell — ilspycmd writes to stderr, always redirect to a file)
ilspycmd -t <FullNamespace.ClassName> <assembly_path> 2>&1 > "$env:TEMP\MyType.cs"

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
    Cli/                   # CLI option parsing
    Extraction/            # Metadata, XML doc, signature decoder, tokenizer & source resolver
    Formatting/            # Text/JSON formatters + System.Text.Json source-gen context
    Models/                # Extracted & query DTO records
    Storage/               # Index catalog, parallel streaming builder, indexed SQLite query store
  ApiLookup.Tests/         # xUnit unit & integration tests
tools/
  build.ps1                # Publishes apilookup binary to csharp-definition-lookup/scripts/
csharp-definition-lookup/
  SKILL.md                 # Skill guide for AI agents
  scripts/
    apilookup.exe          # Prebuilt Windows single-file CLI (framework-dependent)
    TypeScanner.csx        # Legacy dotnet-script scanner (deprecated)
csharp-decompiler/
  SKILL.md                 # Skill guide for AI agents
```

---

## License

[MIT](LICENSE)
