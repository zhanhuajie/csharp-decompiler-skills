---
name: csharp-decompiler
description: Decompile .NET IL-based assemblies (.dll, .exe) to C# source code. Use when analyzing C# binaries, reverse engineering logic, or exporting complete projects from compiled files. Supports listing types, decompiling specific classes, and full project export. If you cannot confirm which DLL contains the target type, use the `csharp-definition-lookup` skill first to locate the exact assembly.
---

# C# Decompiler

Expert tool for reverse engineering .NET assemblies using `ilspycmd`.

## Prerequisites

- **.NET SDK** installed.
- **ilspycmd** installed: `dotnet tool install -g ilspycmd`.

## Locating Assemblies

If you need to find an assembly path manually, search these common locations:

- **Project Output**: `bin/Debug/netX.X/` or `bin/Release/netX.X/`.
- **NuGet Cache**: `%USERPROFILE%/.nuget/packages/` (Windows) or `~/.nuget/packages/` (Linux/macOS).
- **Global Assembly Cache (GAC)**: Use `csharp-definition-lookup` to find paths for standard library components.
- **Runtime Folders**: `C:/Program Files/dotnet/shared/Microsoft.NETCore.App/` for framework DLLs.

## Workflow

> **Note on Output Noise**: `ilspycmd` may print version warning lines such as `not using the latest version of the tool` or `Latest version is X.X.X`. These are harmless and can be ignored when parsing output.

### 1. Verify Environment

Confirm `dotnet` and `ilspycmd` are available:

```
dotnet --version
ilspycmd --version
```

If `ilspycmd` is not found, add the dotnet tools directory to PATH:

- Windows: `%USERPROFILE%\.dotnet\tools`
- Linux/macOS: `~/.dotnet/tools`

### 2. Identify Target Types

List all types (classes, interfaces, enums) within an assembly to find points of interest:
`ilspycmd -l s <assembly_path>`

The output is a flat list of fully qualified type names, one per line:

```
MyCompany.Services.AuthService
MyCompany.Services.UserRepository
MyCompany.Models.UserDto
MyCompany.Models.UserDto+AddressInfo
```

Use this name verbatim as `<full_type_name>` in subsequent commands. Nested classes appear with `+` separating outer and inner class names.

### 3. API Surface Overview (Signatures Only)

For large types with many members, get a quick overview of the public API without reading the full implementation.

> **Note**: `ilspycmd` writes to **stderr**, not stdout. Direct piping is unreliable across PowerShell versions. Always use file redirection.

**Windows (PowerShell):**

```powershell
ilspycmd -t <full_type_name> <assembly_path> 2>&1 > "$env:TEMP\out.cs"
Select-String -Path "$env:TEMP\out.cs" -Pattern '\b(public|protected|private|internal|static|override|virtual|abstract)\b' | Where-Object { $_.Line -notmatch '\{|//' } | Select-Object -ExpandProperty Line
```

**Linux/macOS:**

```bash
ilspycmd -t <full_type_name> <assembly_path> > /tmp/out.cs 2>&1
grep -E '\b(public|protected|private|internal)\b' /tmp/out.cs | grep -v '{' | grep -v '//'
```

This extracts method/property/field declaration lines while skipping implementation bodies and comments.

### 4. Decompile Specific Type

Decompile and save a specific type to a file, then read it with the `read_file` tool:

**Windows (PowerShell):**

```powershell
ilspycmd -t <full_type_name> <assembly_path> 2>&1 > "$env:TEMP\MyType.cs"
```

**Linux/macOS:**

```bash
ilspycmd -t <full_type_name> <assembly_path> > /tmp/MyType.cs 2>&1
```

Then use `read_file` on the output file to retrieve the content. Do not rely on terminal output — it will be truncated for non-trivial types.

> **Note**: For nested classes, use the `+` notation: `ilspycmd -t "Namespace.OuterClass+InnerClass" my.dll 2>&1 > "$env:TEMP\out.cs"`

### 5. Full Project Export

Decompile the entire assembly into a C# project structure (.csproj and .cs files):
`ilspycmd -p -o <output_directory> <assembly_path>`

## Best Practices

- **Narrow Search**: Use `-l s` first to confirm the exact type name before decompiling.
- **Large Types**: For types with 50+ members, use the API Surface Overview step first to understand the structure before reading full source.
- **Full Type Names**: Always include the namespace (e.g., `MyCompany.Services.AuthService`) for precise decompilation.
- **Always Use File Redirection**: All `ilspycmd` commands write to stderr. Always redirect with `2>&1 >` (Windows) or `> file 2>&1` (Linux/macOS) and read the output file. Terminal output will be truncated or empty.

## Troubleshooting & Advanced

- **"Type not found"**: Ensure you're using the fully qualified name (Namespace.ClassName). Use `-l s` to verify the exact name in the assembly. **If `-l s` does not list the type at all** (e.g., in C++/CLI assemblies), still try `-t` directly with the expected fully qualified name — `ilspycmd` may be able to decompile it even when the type doesn't appear in the listing.
- **"Command 'ilspycmd' not found"**: Ensure the tool is in your `PATH` (`~/.dotnet/tools` or `%USERPROFILE%\.dotnet\tools`).
- **Performance and Memory**: Decompiling very large assemblies (>50MB) can be memory-intensive. Prefer decompiling specific types rather than a full export, and use a dedicated output directory for full exports.
- **Obfuscated Assemblies**: If an assembly is obfuscated (e.g., Dotfuscator), decompiled code may have meaningless names (`a`, `b`, `c`). This skill does not provide de-obfuscation services.
- **C++/CLI Mixed Assemblies**: Assemblies compiled as C++/CLI (mixed native + managed) are only partially visible to `ilspycmd`. The `-l s` output may show only a handful of types (typically value structs) and silently skip the majority of managed classes. If `-l s` returns an unexpectedly small type list for a large DLL, the assembly is likely C++/CLI. However, **the managed layer may still be decompilable**: try `-t <full_type_name>` directly with the expected fully qualified name — it often succeeds even when the type was absent from `-l s`. If `-t` also fails, use `csharp-definition-lookup` to probe the assembly, or fall back to ILSpy GUI.
