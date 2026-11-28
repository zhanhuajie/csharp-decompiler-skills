---
name: dotnet-decompiler
description: Decompile .NET assemblies (.dll, .exe, single-file bundles, WPF BAML) to C# source code or IL using ilspycmd. Use when you need method bodies, single-member implementations, internal side effects, embedded XAML resources, or full project export. For locating DLLs/namespaces, clean C# API signatures, XML docs, inheritance, extension methods, or reverse type lookup, use the faster `dotnet-api-lookup` skill first.
---

# .NET Decompiler

Reverse-engineer compiled .NET assemblies (`.dll`, `.exe`, single-file bundles) with `ilspycmd`.

> **Prefer `dotnet-api-lookup` first**: Before decompiling, use `dotnet-api-lookup` (`apilookup.exe`) to find the exact `Assembly` path and `CLR Name`, inspect clean C# signatures/XML docs/inherited members/extensions, or narrow down to the specific member name. Escalate to `ilspycmd` only when you need actual **method bodies, resource XAML, or internal implementation details**.

## Prerequisites & Native AOT Single-File Setup

- **With .NET 10+ SDK (recommended)**:
  ```bash
  dotnet tool install -g ilspycmd
  # or run without installing globally:
  dnx ilspycmd -- --version
  ```
  On `win-x64` and `linux-x64`, `ilspycmd` 11+ automatically installs a standalone **Native AOT single-file** binary (fast startup, no .NET runtime needed after install); other platforms use the framework-dependent package on .NET 10. Upgrade older installs with `dotnet tool update -g ilspycmd` (needed for `-m` and `--resource`).
- **Without .NET 10 SDK (standalone single-file, zero runtime required on `win-x64` / `linux-x64`)**:
  Download the official RID NuGet package directly and extract `tools/any/<rid>/ilspycmd[.exe]`:
  - **Windows (PowerShell)**:
    ```powershell
    Invoke-WebRequest "https://www.nuget.org/api/v2/package/ilspycmd.win-x64" -OutFile "$env:TEMP\ilspycmd.zip"
    Expand-Archive "$env:TEMP\ilspycmd.zip" "$env:TEMP\ilspycmd-pkg" -Force
    $ilspycmd = "$env:TEMP\ilspycmd-pkg\tools\any\win-x64\ilspycmd.exe"
    ```
  - **Linux (`x64`)**:
    ```bash
    curl -L https://www.nuget.org/api/v2/package/ilspycmd.linux-x64 -o /tmp/ilspycmd.zip
    unzip -o /tmp/ilspycmd.zip "tools/any/linux-x64/*" -d /tmp/ilspycmd-pkg
    chmod +x /tmp/ilspycmd-pkg/tools/any/linux-x64/ilspycmd
    ```
- If `ilspycmd` is installed globally but not found, ensure `%USERPROFILE%\.dotnet\tools` (Windows) or `~/.dotnet/tools` (Linux/macOS) is on `PATH`.

## Essential Conventions

- **Silence update checks**: Always pass `--disable-updatecheck` in agent/automated runs to avoid update-check network latency and stderr notices (`not using the latest version of the tool`).
- **Prefer `-o <dir>` over stdout redirection**: `-t` and `-m` accept `-o <dir>` and write `<FullTypeName>.decompiled.cs` directly as clean UTF-8 without BOM. Never merge `2>&1` into `.cs` files — in Windows PowerShell 5.1, `2>&1 >` encodes files as UTF-16 LE, injects `ErrorRecord` text into C# source, and throws when `$ErrorActionPreference = "Stop"`.
- **CLR Names & PowerShell Quoting**:
  - Nested types use `+` (e.g. `'MyNs.Outer+Inner'`).
  - Generic types keep their backtick arity (e.g. ``'MyNs.MyList`1'``). In PowerShell, **always use single quotes** around type names and XML doc IDs—double quotes consume `` ` `` as an escape character.

## Workflow

### 1. Decompile a Single Member (`-m`) — Preferred for Large Classes

Avoid flooding context with 5,000-line SDK classes (such as Revit `Document` or AutoCAD `Database`). Find the member with `apilookup show <Type> --member <part>`, then decompile only that member:

```powershell
# XML doc ID (parameter list & generic arities may be omitted) or metadata token (e.g. 0x06000005)
ilspycmd --disable-updatecheck -m 'M:Namespace.TypeName.MethodName' <assembly_path>
```

Prefixes: `M:` method/constructor, `P:` property, `F:` field, `E:` event.

### 2. Decompile a Specific Type (`-t`)

Use `-o <temp_dir>` to write a clean `.decompiled.cs` file, then read it with `read_file`:

```powershell
ilspycmd --disable-updatecheck -t 'Namespace.OuterClass+InnerClass' -o "$env:TEMP\decompiled" <assembly_path>
# Add -r <dependency_dir> if referenced SDK/NuGet DLLs sit in another directory:
ilspycmd --disable-updatecheck -r "C:\Program Files\VendorSDK" -t 'Namespace.MyGeneric`1' -o "$env:TEMP\decompiled" <assembly_path>
```

### 3. List Types When `dotnet-api-lookup` Is Unavailable (`-l cisde`)

`ilspycmd -l` takes **one concatenated string** of entity codes (`c`lass, `i`nterface, `s`truct, `d`elegate, `e`num)—never use commas, spaces, or `-l s` alone (`-l s` lists structs only and skips all classes/interfaces/enums):

```powershell
ilspycmd --disable-updatecheck -l cisde <assembly_path>
```

### 4. Export a Full Compilable Project (`-p`)

Decompile an entire assembly into a `.csproj` + `.cs` hierarchy (also decompiles WPF `.baml` resources to `.xaml` Page items):

```powershell
ilspycmd --disable-updatecheck -p --nested-directories -o <output_directory> <assembly_path>
```

### 5. Specialized Reverse-Engineering Flags

- **WPF dialogs & XAML (`--list-resources` / `--resource`)**: Find exact dialog class names and UI structure:
  ```powershell
  ilspycmd --disable-updatecheck --list-resources <assembly_path>
  ilspycmd --disable-updatecheck --resource 'MyApp.g.resources/dialogs/exportdialog.baml' -o <output_directory> <assembly_path>
  ```
- **Single-file `.NET` bundles (`-d` / `--bundle-entry`)**: Unpack or target an assembly inside a single-file `.exe`:
  ```powershell
  ilspycmd --disable-updatecheck -d -o <bundle_dir> <app.exe>
  ilspycmd --disable-updatecheck --bundle-entry MyApp.dll -t 'MyApp.Program' <app.exe>
  ```
- **IL fallback & noise reduction**: Use `-il` to inspect raw IL when C# decompilation fails; add `--no-dead-code --no-dead-stores` to strip dead locals/branches.

## Locating Assemblies

Prefer `apilookup locate <TypeName> --project / --dir` first. Manual fallback locations:
- **Project Output**: `bin/Debug/netX.X/` or `bin/Release/netX.X/`.
- **NuGet Cache**: `%USERPROFILE%\.nuget\packages\` (Windows) or `~/.nuget/packages/` (Linux/macOS).
- **Runtime Folders**: `C:\Program Files\dotnet\shared\Microsoft.NETCore.App\` for framework DLLs.

## Troubleshooting

- **"Type not found"**: Check `CLR Name` via `apilookup show` or `ilspycmd --disable-updatecheck -l cisde <dll>`. Remember `+` for nested classes, `` `N `` for generics, and **single quotes** in PowerShell so backticks aren't stripped.
- **Partial / missing types or failed method bodies**: Pass `-r <dep_dir>` for missing dependency folders and `--ignore-decompilation-errors` so one broken method does not fail an export. Fall back to `-il` on the member/type if C# AST recovery fails.
- **Unrecognized `-m` / `--resource` / `--disable-updatecheck`**: Upgrade `ilspycmd` (`dotnet tool update -g ilspycmd`).
- **C++/CLI mixed assemblies**: Native + managed mixed DLLs can produce incomplete C# decompilation or partial `-l` results; try `-t 'Namespace.ClassName'` or `-il` directly, or inspect public metadata with `dotnet-api-lookup` first.
- **Obfuscated assemblies**: Renaming obfuscators produce unreadable identifiers (`a`, `b`, `c`); de-obfuscation is out of scope for this skill.
