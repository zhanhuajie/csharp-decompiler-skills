---
name: csharp-decompiler
description: Decompile .NET IL-based assemblies (.dll, .exe) to C# source code. Use when analyzing C# binaries, reverse engineering logic, or exporting complete projects from compiled files. Supports listing types, decompiling specific classes, and full project export.
---

# C# Decompiler

Expert tool for reverse engineering .NET assemblies using `ilspycmd`.

## Prerequisites
- **.NET SDK** installed.
- **ilspycmd** installed: `dotnet tool install -g ilspycmd`.

## Locating Assemblies

> **Tip**: If you cannot confirm which DLL contains your target type, it is highly recommended to use the `csharp-definition-lookup` skill first to locate the exact assembly containing the type.

If you need to find an assembly path manually, search these common locations:
- **Project Output**: `bin/Debug/netX.X/` or `bin/Release/netX.X/`.
- **NuGet Cache**: `%USERPROFILE%/.nuget/packages/` (Windows) or `~/.nuget/packages/` (Linux/macOS).
- **Global Assembly Cache (GAC)**: Use `csharp-definition-lookup` to find paths for standard library components.
- **Runtime Folders**: `C:/Program Files/dotnet/shared/Microsoft.NETCore.App/` for framework DLLs.

## Workflow

> **Note on Execution Path**: The command examples below use relative paths (`scripts/decompile.py`). When running these commands from outside the skill's root directory, you must replace this with the absolute path to the script (e.g., `C:/path/to/skill/csharp-decompiler/scripts/decompile.py`).

### 1. Verify Environment
Run the check command to ensure `dotnet` and `ilspycmd` are available:
`python scripts/decompile.py check`

### 2. Identify Target Types
List all types (classes, interfaces, enums) within an assembly to find points of interest:
`python scripts/decompile.py list <assembly_path>`

### 3. Decompile Specific Type
Extract the C# source code for a specific type (use full name including namespace):
`python scripts/decompile.py type <assembly_path> <full_type_name>`
> **Note**: For nested classes, use the `+` notation: `python scripts/decompile.py type my.dll "Namespace.OuterClass+InnerClass"`

### 4. Full Project Export
Decompile the entire assembly into a C# project structure (.csproj and .cs files):
`python scripts/decompile.py all <assembly_path> <output_directory>`

## Best Practices
- **Narrow Search**: Use `list` first to avoid massive output from a full export.
- **Full Type Names**: Always include the namespace (e.g., `MyCompany.Services.AuthService`) for precise decompilation.
- **Output Redirection**: For large files, redirect output to a file: `python scripts/decompile.py type my.dll MyType > MyType.cs`.

## Troubleshooting & Advanced
- **"Type not found"**: Ensure you're using the fully qualified name (Namespace.ClassName). Use the `list` command to verify the exact name in the assembly.
- **"Command 'ilspycmd' not found"**: Ensure the tool is in your `PATH` (`~/.dotnet/tools` or `%USERPROFILE%\.dotnet\tools`).
- **Performance and Memory**: Decompiling very large assemblies (>50MB) can be memory-intensive. Prefer decompiling specific types rather than a full export, and use a dedicated output directory for full exports.
- **Obfuscated Assemblies**: If an assembly is obfuscated (e.g., Dotfuscator), decompiled code may have meaningless names (`a`, `b`, `c`). This skill does not provide de-obfuscation services.

## Resources
- **scripts/decompile.py**: Robust wrapper for `ilspycmd`.
