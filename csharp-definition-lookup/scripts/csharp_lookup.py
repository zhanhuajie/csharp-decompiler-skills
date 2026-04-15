import os
import subprocess
import sys
import argparse
import xml.etree.ElementTree as ET
import json


def get_nuget_cache_path():
    """Detect the NuGet global packages folder."""
    nuget_packages = os.environ.get("NUGET_PACKAGES")
    if nuget_packages:
        return nuget_packages
    user_profile = os.environ.get("USERPROFILE") or os.environ.get("HOME")
    if not user_profile:
        return ""
    return os.path.join(user_profile, ".nuget", "packages")


def resolve_from_assets(project_path):
    """Parse obj/project.assets.json and resolve DLL paths. Uses targets for accuracy."""
    obj_dir = os.path.join(os.path.dirname(project_path), "obj")
    assets_path = os.path.join(obj_dir, "project.assets.json")
    if not os.path.exists(assets_path):
        return None, [], {}

    cache_file = os.path.join(obj_dir, ".csharp_lookup_dlls.cache")
    assets_mtime = os.path.getmtime(assets_path)

    # Try reading from cache
    if os.path.exists(cache_file):
        try:
            with open(cache_file, "r", encoding="utf-8") as f:
                cache_data = json.load(f)
                if cache_data.get("mtime") == assets_mtime:
                    return (
                        assets_path,
                        cache_data.get("dlls", []),
                        cache_data.get("groups", {}),
                    )
        except Exception:
            pass  # ignore cache errors

    try:
        with open(assets_path, "r", encoding="utf-8") as f:
            data = json.load(f)

        dlls = []
        package_groups = {}  # pkg_id -> list of dlls
        cache_path = get_nuget_cache_path()

        libraries = data.get("libraries", {})
        targets = data.get("targets", {})

        # We collect exact DLL paths from all targets to cover multi-targeting,
        # which is still vastly smaller than os.walk() on the entire package.
        for tfm, packages in targets.items():
            for pkg_id, pkg_info in packages.items():
                if pkg_info.get("type") != "package":
                    continue

                # Get the base path for this package from libraries
                lib_info = libraries.get(pkg_id, {})
                base_path = lib_info.get("path")
                if not base_path:
                    continue

                pkg_full_path = os.path.join(cache_path, base_path)

                # Extract compile and runtime DLLs
                dll_rel_paths = set()
                compile_dict = pkg_info.get("compile", {})
                runtime_dict = pkg_info.get("runtime", {})

                for rel_path in compile_dict.keys():
                    if rel_path.endswith(".dll"):
                        dll_rel_paths.add(rel_path)

                for rel_path in runtime_dict.keys():
                    if rel_path.endswith(".dll"):
                        dll_rel_paths.add(rel_path)

                if dll_rel_paths:
                    if pkg_id not in package_groups:
                        package_groups[pkg_id] = []

                    for rel_path in dll_rel_paths:
                        full_dll_path = os.path.normpath(
                            os.path.join(pkg_full_path, rel_path)
                        )
                        if (
                            os.path.exists(full_dll_path)
                            and full_dll_path not in package_groups[pkg_id]
                        ):
                            package_groups[pkg_id].append(full_dll_path)
                            if full_dll_path not in dlls:
                                dlls.append(full_dll_path)

        # Write cache
        try:
            with open(cache_file, "w", encoding="utf-8") as f:
                json.dump(
                    {"mtime": assets_mtime, "dlls": dlls, "groups": package_groups}, f
                )
        except Exception as e:
            print(f"Warning: Could not write cache file: {e}", file=sys.stderr)

        return assets_path, dlls, package_groups
    except Exception as e:
        print(f"Error parsing assets file: {e}", file=sys.stderr)
        return assets_path, [], {}


def get_dll_references(project_path):
    """Scan .csproj for DLL references."""
    references = []
    if not project_path or not os.path.exists(project_path):
        return references

    try:
        tree = ET.parse(project_path)
        root = tree.getroot()

        # Csproj uses namespaces often
        ns = {"ns": "http://schemas.microsoft.com/developer/msbuild/2003"}

        # Try both with and without namespace
        ref_elements = root.findall(".//Reference", ns) or root.findall(".//Reference")

        for ref in ref_elements:
            hint_path = ref.find("HintPath", ns)
            if hint_path is None:
                hint_path = ref.find("HintPath")

            if hint_path is not None and hint_path.text:
                # Resolve relative path
                path = hint_path.text
                if not os.path.isabs(path):
                    path = os.path.abspath(
                        os.path.join(os.path.dirname(project_path), path)
                    )
                references.append(path)
    except Exception as e:
        print(f"Error parsing csproj: {e}", file=sys.stderr)

    return list(set(references))


def check_dotnet_script_installed():
    """Check if dotnet script is installed."""
    try:
        result = subprocess.run(
            ["dotnet", "script", "--version"], capture_output=True, text=True
        )
        return result.returncode == 0
    except Exception:
        return False


def fast_scan_dlls(dlls, target_type, namespace_filter=None):
    """Use the C# scanner to instantly find matching DLLs."""
    script_dir = os.path.dirname(os.path.abspath(__file__))
    scanner_script = os.path.join(script_dir, "TypeScanner.csx")

    if not os.path.exists(scanner_script):
        print(
            f"Error: Could not find the C# metadata scanner script at {scanner_script}.",
            file=sys.stderr,
        )
        return []

    if not check_dotnet_script_installed():
        print(
            "Error: 'dotnet-script' is not installed. Please install it by running: dotnet tool install -g dotnet-script",
            file=sys.stderr,
        )
        return []

    try:
        # Pass dll paths via stdin
        input_data = "\n".join(dlls) + "\n"

        cmd = ["dotnet", "script", scanner_script, "--", target_type]
        if namespace_filter:
            cmd.append(namespace_filter)

        result = subprocess.run(
            cmd, input=input_data, text=True, capture_output=True, encoding="utf-8"
        )

        matches = []
        for line in result.stdout.splitlines():
            line = line.strip()
            if not line or "|" not in line:
                continue
            full_name, dll_path = line.split("|", 1)
            matches.append((full_name, dll_path))
        return matches
    except Exception as e:
        print(f"Scanner error: {e}", file=sys.stderr)
        return []


def decompile_type(dll_path, full_type_name):
    """Decompile a specific type from a DLL."""
    try:
        result = subprocess.run(
            ["ilspycmd", "-t", full_type_name, dll_path], capture_output=True, text=True
        )
        if result.returncode == 0:
            return result.stdout
        return f"Error: {result.stderr}"
    except Exception as e:
        return f"Exception: {e}"


def summarize_decompiled(code):
    """Extract only type/member signatures from decompiled output.

    Emits: using directives, namespace/type declarations, fields, auto-properties,
    attributes, abstract/extern method signatures.
    Omits: all member bodies entirely — no folding, no { ... } placeholders.

    Works for both K&R style (brace on same line as signature) and Allman style
    (brace on the line after the signature).
    """
    lines = code.splitlines()
    output = []
    depth = 0
    i = 0

    while i < len(lines):
        line = lines[i]
        stripped = line.strip()
        opens = stripped.count("{")
        closes = stripped.count("}")

        # Outside type body (using / namespace / class declaration level): emit as-is.
        if depth < 2:
            output.append(line)
            depth += opens - closes
            i += 1
            continue

        # depth == 2: member level inside a type.

        # Pure closing brace — closes the type or namespace; emit and drop depth.
        if stripped == "}":
            output.append(line)
            depth += opens - closes  # -1
            i += 1
            continue

        # Blank lines: preserve for readability.
        if not stripped:
            output.append(line)
            i += 1
            continue

        if opens == closes:
            # Balanced line: field, auto-property, attribute, abstract/extern method, etc.
            # Lookahead: if next non-blank line is a lone '{', this is an Allman-style
            # member whose signature sits on this line.
            j = i + 1
            while j < len(lines) and not lines[j].strip():
                j += 1
            if j < len(lines) and lines[j].strip() == "{":
                # Allman-style member: emit signature + ';', then skip body.
                output.append(line.rstrip() + ";")
                i = j + 1  # advance past the opening '{'
                depth += 1  # account for the '{' we skipped
                while i < len(lines) and depth > 2:
                    s = lines[i].strip()
                    depth += s.count("{") - s.count("}")
                    i += 1
            else:
                # Genuine standalone declaration — emit as-is.
                output.append(line)
                i += 1
            continue

        if opens > closes:
            # K&R-style member: signature and '{' are on the same line.
            # Emit only the part before '{' as the signature.
            brace_idx = line.index("{")
            sig = line[:brace_idx].rstrip()
            if sig.strip():
                output.append(sig + ";")
            depth += opens - closes
            i += 1
            # Skip body until we return to member level.
            while i < len(lines) and depth > 2:
                s = lines[i].strip()
                depth += s.count("{") - s.count("}")
                i += 1
            continue

        # closes > opens at depth 2 is malformed; advance safely.
        depth += opens - closes
        i += 1

    return "\n".join(output)


def main():
    parser = argparse.ArgumentParser(
        description="Lookup C# class definitions in project references."
    )
    parser.add_argument("symbol", help="The class name to lookup (e.g., Asset)")
    parser.add_argument(
        "--project", help="Path to the .csproj file to scan for references."
    )
    parser.add_argument("--dll", help="Optional specific DLL path to search in.")
    parser.add_argument(
        "--list-refs",
        action="store_true",
        help="List all resolved DLL references before searching.",
    )
    parser.add_argument(
        "--full",
        action="store_true",
        help="Print full decompiled output without truncating.",
    )
    parser.add_argument(
        "--summary",
        action="store_true",
        help="Show only member signatures (fields, properties, method signatures) — skip method bodies.",
    )
    parser.add_argument(
        "--namespace", help="Filter results by namespace prefix (e.g., Autodesk)."
    )

    args = parser.parse_args()

    if not args.project and not args.dll:
        print(
            "Error: No search source provided. Use --project <path> or --dll <path>.",
            file=sys.stderr,
        )
        return

    dlls_to_check = []
    asset_groups = {}

    if args.dll:
        dlls_to_check.append(os.path.abspath(args.dll))

    if args.project:
        # 1. Try project.assets.json for SDK-style projects
        assets_path, asset_dlls, asset_groups = resolve_from_assets(args.project)
        if asset_dlls:
            dlls_to_check.extend(asset_dlls)

        # 2. Fallback/Additional: Scan csproj for explicit References (Legacy style or local refs)
        dlls_to_check.extend(get_dll_references(args.project))

    # Remove duplicates while preserving order
    seen = set()
    dlls_to_check = [x for x in dlls_to_check if not (x in seen or seen.add(x))]

    if args.project and not dlls_to_check:
        print(
            f"Warning: No DLL references found in project '{args.project}'.",
            file=sys.stderr,
        )
        print(
            "If this is an SDK-style project, ensure you have run 'dotnet restore'.",
            file=sys.stderr,
        )
        return

    if not dlls_to_check:
        print("Error: No DLL references found to search.", file=sys.stderr)
        return

    if args.list_refs:
        print("\nResolved DLL references:")
        if asset_groups:
            for pkg_id, pkg_dlls in asset_groups.items():
                print(f"  {pkg_id} ({len(pkg_dlls)} dlls)")

        # Calculate local/explicit DLLs
        local_dlls = [
            d
            for d in dlls_to_check
            if not any(d in g_dlls for g_dlls in asset_groups.values())
        ]
        if local_dlls:
            print(f"  Local/Explicit References ({len(local_dlls)} dlls)")
        print("")

    print(
        f"Searching for symbol '{args.symbol}' in {len(dlls_to_check)} references using fast metadata scanner..."
    )

    matches = fast_scan_dlls(dlls_to_check, args.symbol, args.namespace)

    if not matches:
        print(f"Symbol '{args.symbol}' not found in the provided references.")
        return

    for full_name, dll in matches:
        print(f"\n[FOUND] {full_name}")
        print(f"Assembly: {dll}")
        print("-" * 40)
        code = decompile_type(dll, full_name)
        if args.summary:
            print(summarize_decompiled(code))
        else:
            lines = code.splitlines()
            limit = 60 if not args.full else len(lines)
            print("\n".join(lines[:limit]))
            if len(lines) > limit:
                print(
                    f"... ({len(lines) - limit} lines truncated, use --full to see all, or --summary for signatures only)"
                )


if __name__ == "__main__":
    main()
