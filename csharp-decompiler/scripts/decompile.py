import subprocess
import sys
import os
import shutil

def run_command(command, check=True):
    """Run a shell command and return stdout/stderr."""
    try:
        result = subprocess.run(command, capture_output=True, text=True, check=check)
        # Filter stdout and stderr for version warnings
        stdout = "\n".join([line for line in result.stdout.splitlines() 
                           if "not using the latest version of the tool" not in line and "Latest version is" not in line])
        stderr = "\n".join([line for line in result.stderr.splitlines() 
                           if "not using the latest version of the tool" not in line and "Latest version is" not in line])
        return stdout, stderr
    except subprocess.CalledProcessError as e:
        # Some output might still be in e.stdout even on error
        stdout = "\n".join([line for line in e.stdout.splitlines() 
                           if "not using the latest version of the tool" not in line and "Latest version is" not in line]) if e.stdout else ""
        stderr = "\n".join([line for line in e.stderr.splitlines() 
                           if "not using the latest version of the tool" not in line and "Latest version is" not in line]) if e.stderr else ""
        return stdout, stderr
    except FileNotFoundError:
        return None, f"Command '{command[0]}' not found."

def check_dependencies():
    """Check if dotnet and ilspycmd are available."""
    # Check dotnet
    dotnet_path = shutil.which("dotnet")
    if not dotnet_path:
        return False, "'.NET SDK' (dotnet) is not installed or not in PATH. Please install it from https://dotnet.microsoft.com/download."

    # Check ilspycmd
    ilspy_path = shutil.which("ilspycmd")
    if not ilspy_path:
        return False, "'ilspycmd' is not installed. You can install it using: 'dotnet tool install -g ilspycmd'. Ensure the dotnet tool path is in your PATH."

    return True, None

def list_types(assembly_path):
    """List all types in the assembly."""
    if not os.path.exists(assembly_path):
        return f"Error: Assembly not found at {assembly_path}"
    
    stdout, stderr = run_command(["ilspycmd", "-l", "s", assembly_path], check=False)
    output = []
    if stdout:
        output.append(stdout)
    if stderr:
        output.append(f"Warnings/Errors:\n{stderr}")
    
    return "\n".join(output) if output else "No output from ilspycmd."

def decompile_type(assembly_path, type_name):
    """Decompile a specific type."""
    if not os.path.exists(assembly_path):
        return f"Error: Assembly not found at {assembly_path}"
    
    stdout, stderr = run_command(["ilspycmd", "-t", type_name, assembly_path], check=False)
    output = []
    if stdout:
        output.append(stdout)
    if stderr:
        output.append(f"Warnings/Errors:\n{stderr}")
    
    return "\n".join(output) if output else f"No output from ilspycmd for type '{type_name}'."

def decompile_project(assembly_path, output_dir):
    """Decompile the entire assembly into a project directory."""
    if not os.path.exists(assembly_path):
        return f"Error: Assembly not found at {assembly_path}"
    
    if not os.path.exists(output_dir):
        os.makedirs(output_dir)
    
    stdout, stderr = run_command(["ilspycmd", "-p", "-o", output_dir, assembly_path], check=False)
    output = []
    if stdout:
        output.append(stdout)
    if stderr:
        output.append(f"Warnings/Errors:\n{stderr}")
    
    if stdout is not None:
        return f"Successfully decompiled assembly to project at: {output_dir}\n" + "\n".join(output)
    else:
        return f"Error exporting project:\n" + "\n".join(output)

if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("Usage: python decompile.py <command> [args...]")
        print("Commands: check, list <path>, type <path> <name>, all <path> <outdir>")
        sys.exit(1)

    cmd = sys.argv[1]

    # Handle 'check' command first
    if cmd == "check":
        ok, err = check_dependencies()
        if ok:
            print("All dependencies are met (dotnet, ilspycmd).")
            sys.exit(0)
        else:
            print(f"Dependency check failed: {err}")
            sys.exit(1)

    # For other commands, ensure we have an assembly path
    if len(sys.argv) < 3:
        print(f"Usage: python decompile.py {cmd} <assembly_path> [extra_args]")
        sys.exit(1)

    path = sys.argv[2]
    
    # Run dependency check before proceeding
    ok, err = check_dependencies()
    if not ok:
        print(f"Error: {err}")
        sys.exit(1)

    if cmd == "list":
        print(list_types(path))
    elif cmd == "type":
        if len(sys.argv) < 4:
            print("Usage: python decompile.py type <assembly_path> <type_name>")
            sys.exit(1)
        print(decompile_type(path, sys.argv[3]))
    elif cmd == "all":
        if len(sys.argv) < 4:
            print("Usage: python decompile.py all <assembly_path> <output_dir>")
            sys.exit(1)
        print(decompile_project(path, sys.argv[3]))
    else:
        print(f"Unknown command: {cmd}")
        sys.exit(1)
