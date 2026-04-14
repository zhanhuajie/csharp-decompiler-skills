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
- Python 3.x

### Verify Installation

```bash
python csharp-decompiler/scripts/decompile.py check
```

---

## csharp-decompiler

Decompile .NET assemblies to C# source code.

### List all types in an assembly

```bash
python csharp-decompiler/scripts/decompile.py list <assembly_path>
```

### Decompile a specific type

```bash
python csharp-decompiler/scripts/decompile.py type <assembly_path> <FullNamespace.ClassName>
```

> For nested classes, use `+` notation: `Namespace.OuterClass+InnerClass`

### Export the full project

```bash
python csharp-decompiler/scripts/decompile.py all <assembly_path> <output_directory>
```

---

## csharp-definition-lookup

Locate a type's namespace and DLL path from project references (`.csproj` / NuGet).

### Basic usage

```bash
python csharp-definition-lookup/scripts/csharp_lookup.py <SymbolName> --project <PathToCsproj>
```

### List all resolved references

```bash
python csharp-definition-lookup/scripts/csharp_lookup.py <SymbolName> --project <PathToCsproj> --list-refs
```

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
  scripts/
    decompile.py         # ilspycmd wrapper script

csharp-definition-lookup/
  SKILL.md               # Skill description and usage (loaded by AI agents)
  scripts/
    csharp_lookup.py     # Symbol lookup script
    TypeScanner.csx      # C# Script helper scanner
```

---

## License

[MIT](LICENSE)
