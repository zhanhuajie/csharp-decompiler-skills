---
name: csharp-definition-lookup
description: Fast lookup of C# class definitions, namespaces, and assemblies from project references. Resolves NuGet packages via project.assets.json (after dotnet restore) and supports --list-refs to verify search sources. Use when you encounter a class name in code and need to know its full namespace, which DLL it belongs to, or its implementation details. Especially useful for large codebases or closed-source SDKs.
---

# C# Definition Lookup

This skill allows you to quickly find where a C# class (symbol) is defined within your project's references. It resolves NuGet packages via `project.assets.json` and parses `.csproj` files for direct references.

## Workflow

### 1. Identify Unknown Symbol
When you see a class name in code (e.g., `Asset`, `Project`) and you don't know its namespace or assembly.

### 2. Run Lookup
Use the lookup script to scan your project's `.csproj` file for references and find the symbol.

**Command:**
`python scripts/csharp_lookup.py <SymbolName> --project <PathToCsproj>`

**Options:**
- `--project <path>`: Path to the `.csproj` file.
- `--dll <path>`: Search a specific DLL directly.
- `--list-refs`: List all resolved DLL references before searching.

**Example:**
`python scripts/csharp_lookup.py Asset --project MyProject.csproj --list-refs`

### 3. Review Results
The tool will output:
- **Full Name**: The fully qualified name (e.g., `MyCompany.Core.Models.Asset`).
- **Assembly**: The path to the DLL containing the class.
- **Source/Metadata**: A decompiled snippet showing the class signature and members.

## Best Practices
- **Restore Dependencies**: For SDK-style projects, ensure you run `dotnet restore` before using this tool. This generates the `obj/project.assets.json` file required to resolve NuGet packages.
- **Use --list-refs**: If a symbol isn't found, use `--list-refs` to verify that all expected assemblies (including NuGet packages) are being searched.
- **Specific Names**: If a class name is common, you will get multiple results. Look at the assembly path to determine the most relevant one.
- **Combine with Decompiler**: Once you have the full name and DLL path from this tool, you can use the `csharp-decompiler` skill for more advanced reverse engineering if needed.

## Common Mistakes
- **Missing assets file**: Forgetting to run `dotnet restore` on a new project, which prevents the tool from finding NuGet dependencies.
- **Case Sensitivity**: While the tool tries to match symbols, it's best to use the exact casing of the class name.
- **Wrong Project**: Providing a `.csproj` that doesn't reference the assembly you're interested in.

## Resources
- **scripts/csharp_lookup.py**: Core lookup utility using `ilspycmd`.
