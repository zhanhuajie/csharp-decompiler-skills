# csharp-decompiler-skills

[中文](README.zh-CN.md)

A collection of agent-agnostic AI skills for C# assembly decompilation and symbol lookup. These skills help you analyze .NET assemblies, reverse-engineer business logic, and quickly locate types by namespace and assembly.

## Skills

| Skill | Description |
|-------|-------------|
| [csharp-decompiler](csharp-decompiler/SKILL.md) | Decompile .NET IL assemblies (`.dll`, `.exe`) to C# source code. Supports listing types, decompiling specific types, and full project export. |
| [csharp-definition-lookup](csharp-definition-lookup/SKILL.md) | Fast lookup of C# class definitions, namespaces, and assemblies from project references. Resolves NuGet packages via `project.assets.json`. |

---

## Getting Started

### Prerequisites

- [.NET SDK](https://dotnet.microsoft.com/download) installed
- **ilspycmd** installed:
  ```bash
  dotnet tool install -g ilspycmd
  ```
- **dotnet-script** installed (required for `csharp-definition-lookup`):
  ```bash
  dotnet tool install -g dotnet-script
  ```

### Verify Installation

```bash
dotnet --version
ilspycmd --version
```

---

## csharp-decompiler

Decompile .NET assemblies to C# source code using `ilspycmd`.

### List all types in an assembly

```bash
ilspycmd -l s <assembly_path>
```

### Decompile a specific type

**Windows (PowerShell):**
```powershell
ilspycmd -t <FullNamespace.ClassName> <assembly_path> 2>&1 > C:\temp\MyType.cs
```

**Linux/macOS:**
```bash
ilspycmd -t <FullNamespace.ClassName> <assembly_path> > /tmp/MyType.cs 2>&1
```

> For nested classes, use `+` notation: `Namespace.OuterClass+InnerClass`

> **Note**: `ilspycmd` writes to stderr. Always use file redirection (`2>&1 >`) and read the output file — do not rely on terminal output.

### Export the full project

```bash
ilspycmd -p -o <output_directory> <assembly_path>
```

---

## csharp-definition-lookup

Locate a type's namespace and DLL path from project references (`.csproj` / NuGet).

### Basic usage

```powershell
dotnet script csharp-definition-lookup/scripts/TypeScanner.csx -- <SymbolName> --project <PathToCsproj>
```

### List all resolved references

```powershell
dotnet script csharp-definition-lookup/scripts/TypeScanner.csx -- <SymbolName> --project <PathToCsproj> --list-refs
```

### Additional options

| Option | Description |
|--------|-------------|
| `--namespace <prefix>` | Filter results by namespace prefix (e.g., `Autodesk`). |
| `--dll <path>` | Search a specific DLL directly instead of resolving from `.csproj`. |

> Run `dotnet restore` first to generate `obj/project.assets.json` for NuGet resolution.

---

## Recommended Workflow

```
Unknown type location
        │
        ▼
csharp-definition-lookup   ──►  Outputs: full namespace + DLL path
        │
        ▼
csharp-decompiler           ──►  Outputs: full C# source / project structure
```

---

## Repository Structure

```
csharp-decompiler/
  SKILL.md               # Skill description and usage (loaded by AI agents)

csharp-definition-lookup/
  SKILL.md               # Skill description and usage (loaded by AI agents)
  scripts/
    TypeScanner.csx      # Complete lookup tool (resolves references + scans PE metadata)
```

---

## License

[MIT](LICENSE)
